using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SnipPin.Core.Configuration;

namespace SnipPin.Core.Services;

/// <summary>历史记录条目</summary>
public class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>历史目录中的图片文件名</summary>
    public string FileName { get; set; } = "";
    /// <summary>截图时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>若已保存到本地，记录完整路径</summary>
    public string? SavedPath { get; set; }
    /// <summary>来源动作描述（完成/钉住/保存/全屏）</summary>
    public string Action { get; set; } = "";
}

/// <summary>归档元数据</summary>
public class ArchiveMeta
{
    /// <summary>累计归档条数</summary>
    public int ArchivedCount { get; set; }
}

/// <summary>
/// 截图历史服务：
/// - 目录可自定义（配置 History.Directory，修改后自动迁移文件）
/// - 活跃列表不设删除上限；超过 ActiveLimit（默认 200）时最旧的图片归档到 archive.zip（不删除数据）
/// - 支持单条删除、按日期清空、全部清空
/// </summary>
public class HistoryService
{
    private readonly ConfigService _config;

    /// <summary>当前实际使用的目录（可能与配置不同步，直到 ApplyConfigDirectory 迁移完成）</summary>
    private string _currentDir;

    private string IndexFile => Path.Combine(_currentDir, "history.json");
    private string ArchivePath => Path.Combine(_currentDir, "archive.zip");
    private string ArchiveMetaFile => Path.Combine(_currentDir, "archive.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>历史条目（新在前）</summary>
    public IReadOnlyList<HistoryEntry> Entries { get; private set; } = new List<HistoryEntry>();

    /// <summary>已归档到压缩包的累计条数</summary>
    public int ArchivedCount { get; private set; }

    /// <summary>归档压缩包路径（供 UI 打开所在文件夹）</summary>
    public string ArchiveZipPath => ArchivePath;

    /// <summary>条目变化（增/删/清空/归档）时触发，UI 刷新</summary>
    public event Action? Changed;

    public HistoryService(ConfigService config)
    {
        _config = config;
        _currentDir = config.Config.History.Directory;
        Load();
    }

    /// <summary>记录一张新截图（记录失败不影响主流程）</summary>
    public void Add(BitmapSource image, string action, string? savedPath = null)
    {
        try
        {
            Directory.CreateDirectory(_currentDir);
            var entry = new HistoryEntry
            {
                FileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png",
                CreatedAt = DateTime.Now,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                SavedPath = savedPath,
                Action = action,
            };

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = new FileStream(
                Path.Combine(_currentDir, entry.FileName), FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }

            var list = Entries.ToList();
            list.Insert(0, entry);
            Entries = list;

            // 超过活跃上限：最旧的归档到压缩包（数据不删除）
            ArchiveOverflow();

            Save();
            Changed?.Invoke();
        }
        catch
        {
            // 历史记录失败不影响截图主流程
        }
    }

    /// <summary>删除一条历史（含图片文件）</summary>
    public void Delete(HistoryEntry entry)
    {
        var list = Entries.ToList();
        if (list.Remove(entry))
        {
            TryDeleteFile(entry);
            Entries = list;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>清空全部历史</summary>
    public void Clear()
    {
        foreach (var e in Entries)
            TryDeleteFile(e);
        Entries = new List<HistoryEntry>();
        Save();
        Changed?.Invoke();
    }

    /// <summary>清空指定日期之前（不含当天 00:00）的历史</summary>
    public int ClearBefore(DateTime cutoff)
    {
        var remove = Entries.Where(e => e.CreatedAt < cutoff).ToList();
        if (remove.Count == 0) return 0;
        foreach (var e in remove)
            TryDeleteFile(e);
        Entries = Entries.Where(e => e.CreatedAt >= cutoff).ToList();
        Save();
        Changed?.Invoke();
        return remove.Count;
    }

    /// <summary>清空指定日期范围 [start 当天 00:00, end 当天 23:59:59] 内的历史</summary>
    public int ClearRange(DateTime start, DateTime end)
    {
        var from = start.Date;
        var to = end.Date.AddDays(1); // 含 end 当天
        var remove = Entries.Where(e => e.CreatedAt >= from && e.CreatedAt < to).ToList();
        if (remove.Count == 0) return 0;
        foreach (var e in remove)
            TryDeleteFile(e);
        Entries = Entries.Where(e => e.CreatedAt < from || e.CreatedAt >= to).ToList();
        Save();
        Changed?.Invoke();
        return remove.Count;
    }

    /// <summary>加载历史图片（缩略图/预览用，可指定解码宽度节省内存）</summary>
    public BitmapImage? LoadImage(HistoryEntry entry, int decodeWidth = 0)
    {
        var path = Path.Combine(_currentDir, entry.FileName);
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
    /// 应用配置中的历史目录：若与当前目录不同，迁移全部数据文件后切换。
    /// 在设置保存后由 MainWindow 调用。
    /// </summary>
    public void ApplyConfigDirectory()
    {
        try
        {
            var target = _config.Config.History.Directory;
            if (string.IsNullOrWhiteSpace(target)) return;
            if (Path.GetFullPath(target).Equals(Path.GetFullPath(_currentDir),
                    StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(target);
            // 迁移图片、索引、归档包与归档元数据
            foreach (var file in Directory.EnumerateFiles(_currentDir))
                File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

            _currentDir = target;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Program.LogError("历史目录迁移", ex);
        }
    }

    /// <summary>打开历史目录（资源管理器）</summary>
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

    // ---------------- 归档 ----------------
    /// <summary>超过活跃上限时，把最旧的图片追加进 archive.zip 并从活跃列表移出</summary>
    private void ArchiveOverflow()
    {
        int limit = _config.Config.History.ActiveLimit;
        if (limit <= 0) return; // 0 = 永不归档

        var list = Entries.ToList();
        if (list.Count <= limit) return;

        // 最旧在列表末尾
        var toArchive = list.Skip(limit).ToList();
        if (toArchive.Count == 0) return;

        try
        {
            using var zip = ZipFile.Open(ArchivePath, ZipArchiveMode.Update);
            foreach (var e in toArchive)
            {
                var src = Path.Combine(_currentDir, e.FileName);
                if (File.Exists(src))
                {
                    // 归档内以时间+Id 命名，保证唯一且可按时间排序
                    var entryName = $"{e.CreatedAt:yyyyMMdd_HHmmss}_{e.Id}.png";
                    zip.CreateEntryFromFile(src, entryName, CompressionLevel.Optimal);
                    TryDeleteFile(e);
                }
                ArchivedCount++;
            }
        }
        catch (Exception ex)
        {
            Program.LogError("历史归档", ex);
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
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(
                    File.ReadAllText(IndexFile)) ?? new List<HistoryEntry>();
                // 过滤掉图片丢失的条目
                Entries = list
                    .Where(e => File.Exists(Path.Combine(_currentDir, e.FileName)))
                    .ToList();
            }
            if (File.Exists(ArchiveMetaFile))
            {
                var meta = JsonSerializer.Deserialize<ArchiveMeta>(File.ReadAllText(ArchiveMetaFile));
                ArchivedCount = meta?.ArchivedCount ?? 0;
            }
        }
        catch
        {
            Entries = new List<HistoryEntry>();
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

    private void SaveArchiveMeta()
    {
        try
        {
            Directory.CreateDirectory(_currentDir);
            File.WriteAllText(ArchiveMetaFile,
                JsonSerializer.Serialize(new ArchiveMeta { ArchivedCount = ArchivedCount }, JsonOptions));
        }
        catch { }
    }

    private void TryDeleteFile(HistoryEntry entry)
    {
        try { File.Delete(Path.Combine(_currentDir, entry.FileName)); } catch { }
    }
}
