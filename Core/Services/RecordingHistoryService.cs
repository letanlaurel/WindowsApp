using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SnipPin.Core.Configuration;

namespace SnipPin.Core.Services;

/// <summary>录屏历史条目</summary>
public class RecordingEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>录屏目录中的 MP4 文件名</summary>
    public string FileName { get; set; } = "";
    /// <summary>缩略图 PNG 文件名（可为空）</summary>
    public string ThumbFileName { get; set; } = "";
    /// <summary>录制时目标窗口标题</summary>
    public string WindowTitle { get; set; } = "";
    /// <summary>录制开始时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>时长（秒）</summary>
    public int DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>文件大小（字节）</summary>
    public long FileSizeBytes { get; set; }
    /// <summary>来自归档包的解压条目（清除搜索时删除，不参与溢出归档）</summary>
    public bool FromArchive { get; set; }
}

/// <summary>
/// 录屏历史服务：MP4 + 缩略图 + JSON 索引。
/// 活跃原生条目超过 ActiveLimit（默认 50）后先攒 ArchiveBuffer 条缓冲不归档，
/// 攒满一次性把超出的最旧条目按录制月份归档到
/// archive_YYYY-MM.zip（包内含 meta.json 条目元数据；MP4 已压缩，用最快压缩级）。
/// 搜索历史月份时可解压对应月包展现到列表；清除搜索即丢弃解压数据（压缩包保留）。
/// </summary>
public class RecordingHistoryService
{
    private const string MetaEntryName = "meta.json";

    private readonly ConfigService _config;
    private string _currentDir;

    /// <summary>已解压到活跃列表的归档月份（yyyy-MM；清除搜索时丢弃）</summary>
    private readonly HashSet<string> _extractedMonths = new();

    private string IndexFile => Path.Combine(_currentDir, "recordings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>录屏条目（新在前）</summary>
    public IReadOnlyList<RecordingEntry> Entries { get; private set; } = new List<RecordingEntry>();

    /// <summary>已归档到月包的累计条数</summary>
    public int ArchivedCount { get; private set; }

    /// <summary>条目变化（增/删/清空/归档/解压）时触发，UI 刷新（可能在录制线程触发）</summary>
    public event Action? Changed;

    public RecordingHistoryService(ConfigService config)
    {
        _config = config;
        _currentDir = config.Config.Recording.Directory;
        Load();
        RecountArchived();
    }

    /// <summary>登记一条录屏（文件由录制服务写入完成后调用）；createdAt 仅测试注入历史时间用</summary>
    public void Add(string mp4Path, string? thumbPath, string title, int durationSeconds, int width, int height,
        DateTime? createdAt = null)
    {
        var entry = new RecordingEntry
        {
            FileName = Path.GetFileName(mp4Path),
            ThumbFileName = thumbPath != null ? Path.GetFileName(thumbPath) : "",
            WindowTitle = title,
            CreatedAt = createdAt ?? DateTime.Now,
            DurationSeconds = Math.Max(0, durationSeconds),
            Width = width,
            Height = height,
            FileSizeBytes = TryFileSize(mp4Path),
        };

        var list = Entries.ToList();
        list.Insert(0, entry);
        Entries = list;

        // 超过活跃上限：最旧的按月归档到压缩包（数据不删除）
        ArchiveOverflow();

        Save();
        Changed?.Invoke();
    }

    /// <summary>删除一条录屏（含 MP4 与缩略图文件；解压条目同步从归档包移除）</summary>
    public void Delete(RecordingEntry entry)
    {
        var list = Entries.ToList();
        if (list.Remove(entry))
        {
            if (entry.FromArchive)
                RemoveFromArchive(entry);
            TryDeleteFile(entry.FileName);
            if (entry.ThumbFileName.Length > 0)
                TryDeleteFile(entry.ThumbFileName);
            Entries = list;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>清空全部录屏（仅活跃列表文件一并删除，归档包不动）</summary>
    public void Clear()
    {
        foreach (var e in Entries)
        {
            TryDeleteFile(e.FileName);
            if (e.ThumbFileName.Length > 0)
                TryDeleteFile(e.ThumbFileName);
        }
        Entries = new List<RecordingEntry>();
        _extractedMonths.Clear();
        Save();
        Changed?.Invoke();
    }

    /// <summary>条目对应的 MP4 完整路径</summary>
    public string FilePath(RecordingEntry entry) => Path.Combine(_currentDir, entry.FileName);

    /// <summary>加载缩略图（可指定解码宽度节省内存）</summary>
    public BitmapImage? LoadThumb(RecordingEntry entry, int decodeWidth = 0)
    {
        if (entry.ThumbFileName.Length == 0) return null;
        var path = Path.Combine(_currentDir, entry.ThumbFileName);
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

    /// <summary>应用设置中变更后的录屏目录（迁移 MP4、缩略图、月包与索引）。
    /// 只搬本服务管理的文件（rec_*.mp4 / rec_*.png / archive_YYYY-MM.zip / recordings.json），
    /// 其余文件留在原目录——目录可能被指到含其他文件的位置（如源码目录），不能整体搬走。</summary>
    public void ApplyConfigDirectory()
    {
        try
        {
            var target = _config.Config.Recording.Directory;
            if (string.IsNullOrWhiteSpace(target)) return;
            if (Path.GetFullPath(target).Equals(Path.GetFullPath(_currentDir),
                    StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(_currentDir))
            {
                string name = Path.GetFileName(file);
                bool managed = name.Equals("recordings.json", StringComparison.OrdinalIgnoreCase)
                               || (name.StartsWith("rec_", StringComparison.OrdinalIgnoreCase)
                                   && (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                                       || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                               || (name.StartsWith("archive_", StringComparison.OrdinalIgnoreCase)
                                   && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (!managed) continue;
                File.Move(file, Path.Combine(target, name), overwrite: true);
            }

            _currentDir = target;
            Load();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Program.LogError("录屏目录迁移", ex);
        }
    }

    /// <summary>打开录屏目录（资源管理器）</summary>
    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_currentDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _currentDir,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    // ---------------- 归档（按月压缩包） ----------------

    /// <summary>把搜索范围覆盖到的归档月份解压到活跃列表（幂等：同月只解压一次）。
    /// MP4/缩略图落地当前目录，条目按时间合入并标记 FromArchive；压缩包保留。</summary>
    public void EnsureArchivesExtracted(DateTime from, DateTime to)
    {
        bool changed = false;
        foreach (var (month, path) in FindMonthArchives())
        {
            // 月份与搜索范围无交集则跳过（月包覆盖整个自然月；搜索上界为 to 当天结束）
            var monthStart = new DateTime(month.Year, month.Month, 1);
            var monthEnd = monthStart.AddMonths(1);
            if (monthEnd <= from.Date || monthStart >= to.Date.AddDays(1)) continue;

            if (!_extractedMonths.Add(month.ToString("yyyy-MM"))) continue;

            try
            {
                using var zip = ZipFile.OpenRead(path);
                var meta = ReadMeta(zip);
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name == MetaEntryName || entry.Name.Length == 0) continue;
                    entry.ExtractToFile(Path.Combine(_currentDir, entry.Name), overwrite: true);
                }

                var list = Entries.ToList();
                var knownIds = list.Select(e => e.Id).ToHashSet();
                foreach (var m in meta.Where(m => !knownIds.Contains(m.Id)))
                {
                    m.FromArchive = true;
                    list.Add(m);
                }
                list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt)); // 新在前
                Entries = list;
                changed = true;
            }
            catch (Exception ex)
            {
                Program.LogError("录屏归档解压", ex);
            }
        }

        if (changed)
        {
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>丢弃全部解压数据（清除搜索时调用）：删除解压的条目与文件，压缩包保留不动</summary>
    public void DiscardExtracted()
    {
        _extractedMonths.Clear();
        var removed = Entries.Where(e => e.FromArchive).ToList();
        if (removed.Count == 0) return;
        foreach (var e in removed)
        {
            TryDeleteFile(e.FileName);
            if (e.ThumbFileName.Length > 0)
                TryDeleteFile(e.ThumbFileName);
        }
        Entries = Entries.Where(e => !e.FromArchive).ToList();
        Save();
        Changed?.Invoke();
    }

    /// <summary>超过活跃上限后先攒缓冲条数，攒满一次性归档：原生条目达到 limit+缓冲 时，
    /// 把超出 limit 的最旧条目按录制月份追加进对应 archive_YYYY-MM.zip。
    /// 解压条目（FromArchive）不参与归档，等待清除搜索时丢弃。</summary>
    private void ArchiveOverflow()
    {
        int limit = _config.Config.Recording.ActiveLimit;
        if (limit <= 0) return; // 0 = 永不归档

        int buffer = Math.Max(0, _config.Config.Recording.ArchiveBuffer);
        var native = Entries.Where(e => !e.FromArchive).ToList();
        if (native.Count < limit + buffer) return; // 未攒满缓冲区：暂不归档

        var toArchive = native.Skip(limit).ToList(); // 最旧在末尾
        var archivedIds = new HashSet<string>();
        foreach (var g in toArchive.GroupBy(e => new DateTime(e.CreatedAt.Year, e.CreatedAt.Month, 1)))
        {
            // 按月组独立归档：某组失败（如 zip 被占用）不影响其他组，
            // 失败组保留在活跃列表，下次触发时重试
            try
            {
                AppendToMonthArchive(g.Key, g.ToList());
                foreach (var e in g) archivedIds.Add(e.Id);
                ArchivedCount += g.Count();
            }
            catch (Exception ex)
            {
                Program.LogError("录屏归档", ex);
            }
        }
        Entries = Entries.Where(e => !archivedIds.Contains(e.Id)).ToList();
    }

    /// <summary>把一批条目追加进指定月份的压缩包（MP4/缩略图 + meta.json；同名数据幂等覆盖）。
    /// MP4 本身已压缩，用最快压缩级避免白白耗时。</summary>
    private void AppendToMonthArchive(DateTime month, List<RecordingEntry> entries)
    {
        var path = MonthArchivePath(month);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var meta = ReadMeta(zip);

        foreach (var e in entries)
        {
            foreach (var name in new[] { e.FileName, e.ThumbFileName })
            {
                if (name.Length == 0) continue;
                var src = Path.Combine(_currentDir, name);
                if (!File.Exists(src)) continue;
                zip.GetEntry(name)?.Delete(); // 幂等：同名旧条目覆盖
                zip.CreateEntryFromFile(src, name, CompressionLevel.Fastest);
                File.Delete(src);
            }
        }

        var ids = entries.Select(e => e.Id).ToHashSet();
        meta.RemoveAll(m => ids.Contains(m.Id));
        meta.AddRange(entries.Select(e => new RecordingEntry
        {
            Id = e.Id,
            FileName = e.FileName,
            ThumbFileName = e.ThumbFileName,
            WindowTitle = e.WindowTitle,
            CreatedAt = e.CreatedAt,
            DurationSeconds = e.DurationSeconds,
            Width = e.Width,
            Height = e.Height,
            FileSizeBytes = e.FileSizeBytes,
            FromArchive = false,
        }));
        WriteMeta(zip, meta);
    }

    /// <summary>从条目所在月份的压缩包移除该条目（单条删除解压条目时同步，避免再搜回来）</summary>
    private void RemoveFromArchive(RecordingEntry entry)
    {
        try
        {
            var path = MonthArchivePath(entry.CreatedAt);
            if (!File.Exists(path)) return;
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            zip.GetEntry(entry.FileName)?.Delete();
            if (entry.ThumbFileName.Length > 0)
                zip.GetEntry(entry.ThumbFileName)?.Delete();
            var meta = ReadMeta(zip);
            if (meta.RemoveAll(m => m.Id == entry.Id) > 0)
                ArchivedCount--;
            WriteMeta(zip, meta);
        }
        catch (Exception ex)
        {
            Program.LogError("录屏归档条目移除", ex);
        }
    }

    private string MonthArchivePath(DateTime month) =>
        Path.Combine(_currentDir, $"archive_{month:yyyy-MM}.zip");

    /// <summary>扫描目录中的全部月包（按文件名解析月份，新到旧排序）</summary>
    private List<(DateTime Month, string Path)> FindMonthArchives()
    {
        var result = new List<(DateTime, string)>();
        try
        {
            if (!Directory.Exists(_currentDir)) return result;
            foreach (var file in Directory.EnumerateFiles(_currentDir, "archive_????-??.zip"))
            {
                var stem = Path.GetFileNameWithoutExtension(file); // archive_2026-09
                var parts = stem["archive_".Length..].Split('-');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int y) && int.TryParse(parts[1], out int m)
                    && m >= 1 && m <= 12)
                    result.Add((new DateTime(y, m, 1), file));
            }
        }
        catch { }
        return result.OrderByDescending(r => r.Item1).ToList();
    }

    /// <summary>读取月包内 meta.json（缺失或损坏返回空表）</summary>
    private static List<RecordingEntry> ReadMeta(ZipArchive zip)
    {
        var entry = zip.GetEntry(MetaEntryName);
        if (entry == null) return new List<RecordingEntry>();
        try
        {
            using var sr = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<List<RecordingEntry>>(sr.ReadToEnd())
                   ?? new List<RecordingEntry>();
        }
        catch
        {
            return new List<RecordingEntry>();
        }
    }

    /// <summary>重写月包内 meta.json</summary>
    private static void WriteMeta(ZipArchive zip, List<RecordingEntry> meta)
    {
        zip.GetEntry(MetaEntryName)?.Delete();
        var entry = zip.CreateEntry(MetaEntryName);
        using var sw = new StreamWriter(entry.Open());
        sw.Write(JsonSerializer.Serialize(meta, JsonOptions));
    }

    /// <summary>从全部月包 meta.json 统计归档条数</summary>
    private void RecountArchived()
    {
        int count = 0;
        foreach (var (_, path) in FindMonthArchives())
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                count += ReadMeta(zip).Count;
            }
            catch { }
        }
        ArchivedCount = count;
    }

    // ---------------- 持久化 ----------------
    private void Load()
    {
        try
        {
            if (File.Exists(IndexFile))
            {
                var list = JsonSerializer.Deserialize<List<RecordingEntry>>(
                    File.ReadAllText(IndexFile)) ?? new List<RecordingEntry>();
                // 过滤掉视频文件已丢失的条目
                Entries = list
                    .Where(e => File.Exists(Path.Combine(_currentDir, e.FileName)))
                    .ToList();
                // 恢复已解压月份集合（用于幂等判断）
                _extractedMonths.Clear();
                foreach (var month in Entries.Where(e => e.FromArchive)
                             .Select(e => e.CreatedAt.ToString("yyyy-MM")).Distinct())
                    _extractedMonths.Add(month);
            }
        }
        catch
        {
            Entries = new List<RecordingEntry>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_currentDir);
            File.WriteAllText(IndexFile, JsonSerializer.Serialize(Entries.ToList(), JsonOptions));
        }
        catch
        {
            // 索引写入失败不致命
        }
    }

    private static long TryFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private void TryDeleteFile(string fileName)
    {
        try { File.Delete(Path.Combine(_currentDir, fileName)); } catch { }
    }
}
