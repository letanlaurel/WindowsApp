using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnipPin.Core.Configuration;
using SnipPin.Core.Native;

namespace SnipPin.Core.Services;

/// <summary>剪贴板条目类型</summary>
public enum ClipboardEntryType
{
    /// <summary>纯文本</summary>
    Text = 0,
    /// <summary>图片（PNG 文件存储）</summary>
    Image = 1,
    /// <summary>复制的文件列表（Text 中每行一个路径）</summary>
    Files = 2,
}

/// <summary>剪贴板历史条目</summary>
public class ClipboardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipboardEntryType Type { get; set; } = ClipboardEntryType.Text;
    /// <summary>文本内容（Files 时为每行一个源文件完整路径）；大文本仅存前缀预览，完整内容在 TextFileName 文件中</summary>
    public string Text { get; set; } = "";
    /// <summary>图片文件名（仅 Image，位于剪贴板数据目录）</summary>
    public string? FileName { get; set; }
    /// <summary>大文本落盘文件名（仅 Text 超过阈值时，完整内容存 .txt）</summary>
    public string? TextFileName { get; set; }
    /// <summary>复制时间（精确到秒）</summary>
    public DateTime CopiedAt { get; set; } = DateTime.Now;
    /// <summary>图片像素宽（仅 Image，兼作去重指纹）</summary>
    public int Width { get; set; }
    /// <summary>图片像素高（仅 Image，兼作去重指纹）</summary>
    public int Height { get; set; }
}

/// <summary>剪贴板归档元数据</summary>
public class ClipboardArchiveMeta
{
    /// <summary>累计归档条数</summary>
    public int ArchivedCount { get; set; }
}

/// <summary>
/// 剪贴板历史服务：
/// - 通过 AddClipboardFormatListener 监听系统剪贴板（WM_CLIPBOARDUPDATE 由 MainWindow 转发）
/// - 记录文本 / 图片（存 PNG 文件）/ 文件列表，时间精确到秒，相同内容去重
/// - 超过 MaxEntries（默认 500）时最旧的归档到 archive.zip 压缩包（数据不删除）
/// - 支持单条删除、选中写回剪贴板（粘贴用）
/// </summary>
public class ClipboardService
{
    private readonly ConfigService _config;
    private readonly string _dir;

    /// <summary>大文本阈值：超过则完整内容落盘 .txt，索引仅存预览（字符数）</summary>
    private const int LargeTextThreshold = 4096;

    /// <summary>大文本在索引中保留的预览长度（字符数）</summary>
    private const int LargeTextPreviewLength = 2000;

    private string IndexFile => Path.Combine(_dir, "clipboard.json");
    private string ArchivePath => Path.Combine(_dir, "archive.zip");
    private string ArchiveMetaFile => Path.Combine(_dir, "archive.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private IntPtr _hwnd;
    private bool _listening;

    /// <summary>忽略下一次剪贴板更新（自己写回剪贴板触发，避免重复记录）</summary>
    private bool _suppressNext;

    /// <summary>防抖：一次复制常触发多次格式更新（如 Office），合并为一次读取</summary>
    private readonly DispatcherTimer _debounce;

    /// <summary>当前条目（新在前）</summary>
    public IReadOnlyList<ClipboardEntry> Entries { get; private set; } = new List<ClipboardEntry>();

    /// <summary>已归档到压缩包的累计条数</summary>
    public int ArchivedCount { get; private set; }

    /// <summary>条目变化（增/删/置顶/归档）时触发，UI 刷新</summary>
    public event Action? Changed;

    public ClipboardService(ConfigService config)
    {
        _config = config;
        _dir = config.Config.Clipboard.Directory;
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            CaptureNow();
        };
        Load();
    }

    // ---------------- 监听 ----------------

    /// <summary>挂载剪贴板监听（由 MainWindow 在初始化时调用，复用其窗口句柄）</summary>
    public void Start(IntPtr hwnd)
    {
        if (_listening) return;
        _hwnd = hwnd;
        try
        {
            _listening = NativeMethods.AddClipboardFormatListener(hwnd);
            if (!_listening)
                Program.LogError("剪贴板监听", "AddClipboardFormatListener 失败");
        }
        catch (Exception ex)
        {
            Program.LogError("剪贴板监听", ex);
        }
    }

    /// <summary>卸载剪贴板监听（退出时调用）</summary>
    public void Stop()
    {
        if (!_listening) return;
        _listening = false;
        _debounce.Stop();
        try { NativeMethods.RemoveClipboardFormatListener(_hwnd); }
        catch { /* 句柄可能已销毁 */ }
    }

    /// <summary>由 MainWindow 的 WndProc 转发 WM_CLIPBOARDUPDATE 到此</summary>
    public void OnClipboardUpdate()
    {
        if (!_config.Config.Clipboard.Enabled) return;
        if (_suppressNext)
        {
            _suppressNext = false;
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>立即读取剪贴板并记录（防抖到期后调用，必须在 UI 线程）</summary>
    private void CaptureNow()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null) AddImage(img);
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (string? f in files)
                    {
                        if (string.IsNullOrEmpty(f)) continue;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(f);
                    }
                    AddText(sb.ToString(), ClipboardEntryType.Files);
                }
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                    AddText(text, ClipboardEntryType.Text);
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 剪贴板被其他程序占用：放弃本次（下次复制会再记录）
        }
        catch (Exception ex)
        {
            Program.LogError("剪贴板记录", ex);
        }
    }

    // ---------------- 记录 ----------------

    /// <summary>记录文本/文件列表条目（与最新一条相同则跳过）</summary>
    private void AddText(string text, ClipboardEntryType type)
    {
        var list = Entries.ToList();

        // 大文本：完整内容落盘 .txt，索引只存预览，避免超大内容撑爆索引 JSON
        string? textFile = null;
        string preview = text;
        if (type == ClipboardEntryType.Text && text.Length > LargeTextThreshold)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                textFile = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..8]}.txt";
                File.WriteAllText(Path.Combine(_dir, textFile), text);
                preview = text[..LargeTextPreviewLength];
            }
            catch (Exception ex)
            {
                Program.LogError("剪贴板大文本落盘", ex);
                textFile = null; // 落盘失败则退回直接内联存储
                preview = text;
            }
        }

        // 去重：小文本直接比对；大文本按 预览长度 + 文件大小 指纹比对
        if (list.Count > 0 && list[0].Type == type)
        {
            bool duplicate;
            if (textFile == null)
            {
                duplicate = list[0].TextFileName == null && list[0].Text == text;
            }
            else
            {
                var lastFile = Path.Combine(_dir, list[0].TextFileName ?? "");
                duplicate = list[0].TextFileName != null && File.Exists(lastFile)
                            && list[0].Text.Length == preview.Length
                            && new FileInfo(lastFile).Length == new FileInfo(Path.Combine(_dir, textFile)).Length;
            }
            if (duplicate)
            {
                TryDeleteFile(textFile);
                return; // 与最新一条相同，不新增
            }
        }

        list.Insert(0, new ClipboardEntry
        {
            Type = type,
            Text = preview,
            TextFileName = textFile,
            CopiedAt = DateTime.Now,
        });
        Entries = list;
        ArchiveOverflow();
        Save();
        Changed?.Invoke();
    }

    /// <summary>记录图片条目：保存 PNG 后入列表（按 尺寸+文件大小 指纹去重）</summary>
    private void AddImage(BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var entry = new ClipboardEntry
            {
                Type = ClipboardEntryType.Image,
                FileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..8]}.png",
                CopiedAt = DateTime.Now,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
            };

            var path = Path.Combine(_dir, entry.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }

            // 去重指纹：与最新一张图片 尺寸 + 文件大小 一致视为相同内容
            var list = Entries.ToList();
            var lastImage = list.FirstOrDefault(e => e.Type == ClipboardEntryType.Image);
            if (lastImage != null && lastImage.Width == entry.Width && lastImage.Height == entry.Height)
            {
                var prev = Path.Combine(_dir, lastImage.FileName ?? "");
                if (File.Exists(prev) && new FileInfo(prev).Length == new FileInfo(path).Length)
                {
                    TryDeleteFile(entry.FileName);
                    return; // 重复图片，不新增
                }
            }

            list.Insert(0, entry);
            Entries = list;
            ArchiveOverflow();
            Save();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Program.LogError("剪贴板图片记录", ex);
        }
    }

    /// <summary>删除一条记录（含图片 / 大文本 txt 文件）</summary>
    public void Delete(ClipboardEntry entry)
    {
        var list = Entries.ToList();
        if (list.Remove(entry))
        {
            DeleteEntryFiles(entry);
            Entries = list;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>批量删除多条记录（含图片 / 大文本 txt 文件），只保存 / 通知一次</summary>
    public void DeleteRange(IEnumerable<ClipboardEntry> entries)
    {
        var set = entries.ToHashSet();
        var list = Entries.ToList();
        int removed = list.RemoveAll(e => set.Contains(e));
        if (removed == 0) return;

        foreach (var entry in set)
            DeleteEntryFiles(entry);

        Entries = list;
        Save();
        Changed?.Invoke();
    }

    /// <summary>清理条目关联的落盘文件（图片 PNG / 大文本 txt）</summary>
    private void DeleteEntryFiles(ClipboardEntry entry)
    {
        if (entry.Type == ClipboardEntryType.Image)
            TryDeleteFile(entry.FileName);
        TryDeleteFile(entry.TextFileName);
    }

    /// <summary>把条目移到列表顶部（选中粘贴后调用，不触发重新记录）</summary>
    public void Promote(ClipboardEntry entry)
    {
        var list = Entries.ToList();
        if (list.Remove(entry))
        {
            list.Insert(0, entry);
            Entries = list;
            Save();
            Changed?.Invoke();
        }
    }

    // ---------------- 读取 / 粘贴 ----------------

    /// <summary>加载条目图片（缩略图/预览用，可指定解码宽度节省内存）</summary>
    public BitmapImage? LoadImage(ClipboardEntry entry, int decodeWidth = 0)
    {
        if (entry.Type != ClipboardEntryType.Image || string.IsNullOrEmpty(entry.FileName))
            return null;
        var path = Path.Combine(_dir, entry.FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 取条目的完整文本：大文本从落盘 txt 提取，普通文本直接返回。
    /// txt 读取失败时退回索引预览（宁可粘贴预览也不失败）。
    /// </summary>
    public string GetFullText(ClipboardEntry entry)
    {
        if (string.IsNullOrEmpty(entry.TextFileName)) return entry.Text;
        try
        {
            var path = Path.Combine(_dir, entry.TextFileName);
            return File.Exists(path) ? File.ReadAllText(path) : entry.Text;
        }
        catch
        {
            return entry.Text;
        }
    }

    /// <summary>
    /// 把条目写回系统剪贴板（粘贴前置步骤）。
    /// 设置抑制标志，避免自己写入被再次记录。
    /// </summary>
    public void SetEntryToClipboard(ClipboardEntry entry)
    {
        _suppressNext = true;
        try
        {
            switch (entry.Type)
            {
                case ClipboardEntryType.Text:
                    Clipboard.SetText(GetFullText(entry));
                    break;
                case ClipboardEntryType.Files:
                    var files = new System.Collections.Specialized.StringCollection();
                    files.AddRange(entry.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                    Clipboard.SetFileDropList(files);
                    break;
                case ClipboardEntryType.Image:
                    var img = LoadImage(entry);
                    if (img != null)
                        Clipboard.SetImage(img);
                    else
                        _suppressNext = false; // 图片丢失则不抑制后续更新
                    break;
            }
        }
        catch
        {
            _suppressNext = false;
        }
    }

    // ---------------- 归档（压缩存储） ----------------

    /// <summary>超过上限时，最旧的条目归档到 archive.zip（文本进 entries.jsonl，图片进 img/）</summary>
    private void ArchiveOverflow()
    {
        int limit = _config.Config.Clipboard.MaxEntries;
        if (limit <= 0) return; // 0 = 永不归档

        var list = Entries.ToList();
        if (list.Count <= limit) return;

        var toArchive = list.Skip(limit).ToList(); // 最旧在末尾
        if (toArchive.Count == 0) return;

        try
        {
            using var zip = ZipFile.Open(ArchivePath, ZipArchiveMode.Update);

            // 归档文本索引：读取已有 jsonl 追加
            var jsonl = zip.GetEntry("entries.jsonl") ?? zip.CreateEntry("entries.jsonl");
            var lines = new List<string>();
            using (var reader = new StreamReader(jsonl.Open()))
                while (reader.ReadLine() is { } line)
                    lines.Add(line);

            foreach (var e in toArchive)
            {
                if (e.Type == ClipboardEntryType.Image && !string.IsNullOrEmpty(e.FileName))
                {
                    var src = Path.Combine(_dir, e.FileName);
                    if (File.Exists(src))
                    {
                        zip.CreateEntryFromFile(src, $"img/{e.FileName}", CompressionLevel.Optimal);
                        TryDeleteFile(e.FileName);
                    }
                }
                // 大文本 txt 一并压缩归档
                if (!string.IsNullOrEmpty(e.TextFileName))
                {
                    var src = Path.Combine(_dir, e.TextFileName);
                    if (File.Exists(src))
                    {
                        zip.CreateEntryFromFile(src, $"txt/{e.TextFileName}", CompressionLevel.Optimal);
                        TryDeleteFile(e.TextFileName);
                    }
                }
                lines.Add(JsonSerializer.Serialize(e));
                ArchivedCount++;
            }

            using (var writer = new StreamWriter(jsonl.Open()))
                foreach (var line in lines)
                    writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            Program.LogError("剪贴板归档", ex);
        }

        Entries = list.Take(limit).ToList();
        SaveArchiveMeta();
    }

    // ---------------- 持久化 ----------------

    private void Load()
    {
        try
        {
            if (File.Exists(IndexFile))
            {
                var list = JsonSerializer.Deserialize<List<ClipboardEntry>>(
                    File.ReadAllText(IndexFile)) ?? new List<ClipboardEntry>();
                // 过滤掉 图片 / 大文本 txt 文件丢失的条目（保证粘贴内容完整）
                Entries = list.Where(e =>
                    (e.Type != ClipboardEntryType.Image ||
                     (!string.IsNullOrEmpty(e.FileName) &&
                      File.Exists(Path.Combine(_dir, e.FileName)))) &&
                    (string.IsNullOrEmpty(e.TextFileName) ||
                     File.Exists(Path.Combine(_dir, e.TextFileName)))).ToList();
            }
            if (File.Exists(ArchiveMetaFile))
            {
                var meta = JsonSerializer.Deserialize<ClipboardArchiveMeta>(
                    File.ReadAllText(ArchiveMetaFile));
                ArchivedCount = meta?.ArchivedCount ?? 0;
            }
        }
        catch
        {
            Entries = new List<ClipboardEntry>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(IndexFile, JsonSerializer.Serialize(Entries.ToList(), JsonOptions));
        }
        catch
        {
            // 索引写入失败不致命
        }
    }

    private void SaveArchiveMeta()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ArchiveMetaFile,
                JsonSerializer.Serialize(new ClipboardArchiveMeta { ArchivedCount = ArchivedCount }, JsonOptions));
        }
        catch { }
    }

    private void TryDeleteFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        try { File.Delete(Path.Combine(_dir, fileName)); } catch { }
    }
}
