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
    /// <summary>来自归档包的解压条目（清除搜索时删除，不参与溢出归档）</summary>
    public bool FromArchive { get; set; }
}

/// <summary>
/// 截图历史服务：
/// - 目录可自定义（配置 History.Directory，修改后自动迁移数据文件）
/// - 活跃原生条目超过 ActiveLimit（默认 500）后先攒 ArchiveBuffer 条缓冲不归档，
///   攒满一次性把超出的最旧条目按截图月份归档到
///   archive_YYYY-MM.zip（数据不删除，包内含 meta.json 条目元数据）
/// - 搜索历史月份时可解压对应月包展现到列表；清除搜索即丢弃解压数据（压缩包保留）
/// - 支持单条删除（解压条目同步从包内移除）、按日期清空、全部清空
/// </summary>
public class HistoryService
{
    private const string MetaEntryName = "meta.json";

    private readonly ConfigService _config;

    /// <summary>当前实际使用的目录（可能与配置不同步，直到 ApplyConfigDirectory 迁移完成）</summary>
    private string _currentDir;

    /// <summary>已解压到活跃列表的归档月份（yyyy-MM；清除搜索时丢弃）</summary>
    private readonly HashSet<string> _extractedMonths = new();

    private string IndexFile => Path.Combine(_currentDir, "history.json");
    private string LegacyArchivePath => Path.Combine(_currentDir, "archive.zip");
    private string LegacyArchiveMetaFile => Path.Combine(_currentDir, "archive.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>历史条目（新在前）</summary>
    public IReadOnlyList<HistoryEntry> Entries { get; private set; } = new List<HistoryEntry>();

    /// <summary>已归档到月包的累计条数（加载/归档/删除时维护）</summary>
    public int ArchivedCount { get; private set; }

    /// <summary>归档压缩包所在目录（供 UI 打开所在文件夹）</summary>
    public string ArchiveZipPath => _currentDir;

    /// <summary>条目变化（增/删/清空/归档/解压）时触发，UI 刷新</summary>
    public event Action? Changed;

    public HistoryService(ConfigService config)
    {
        _config = config;
        _currentDir = config.Config.History.Directory;
        Load();
        MigrateLegacyArchive();
        RecountArchived();
    }

    /// <summary>记录一张新截图（记录失败不影响主流程）；createdAt 仅测试注入历史时间用</summary>
    public void Add(BitmapSource image, string action, string? savedPath = null, DateTime? createdAt = null)
    {
        try
        {
            Directory.CreateDirectory(_currentDir);
            var t = createdAt ?? DateTime.Now;
            var entry = new HistoryEntry
            {
                FileName = $"{t:yyyyMMdd_HHmmss_fff}.png",
                CreatedAt = t,
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

            // 超过活跃上限：最旧的按月归档到压缩包（数据不删除）
            ArchiveOverflow();

            Save();
            Changed?.Invoke();
        }
        catch
        {
            // 历史记录失败不影响截图主流程
        }
    }

    /// <summary>删除一条历史（含图片文件；解压条目同步从归档包移除）</summary>
    public void Delete(HistoryEntry entry)
    {
        var list = Entries.ToList();
        if (list.Remove(entry))
        {
            if (entry.FromArchive)
                RemoveFromArchive(entry);
            TryDeleteFile(entry);
            Entries = list;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>清空全部历史（仅活跃列表，归档包不动）</summary>
    public void Clear()
    {
        foreach (var e in Entries)
            TryDeleteFile(e);
        Entries = new List<HistoryEntry>();
        _extractedMonths.Clear();
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
    /// 在设置保存后由 MainWindow 调用。只搬本服务管理的文件。
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
            // 迁移图片（yyyyMMdd_ 前缀）、索引、月包/旧归档包与旧归档元数据
            foreach (var file in Directory.EnumerateFiles(_currentDir))
            {
                string name = Path.GetFileName(file);
                bool managed = name.Equals("history.json", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("archive.json", StringComparison.OrdinalIgnoreCase)
                               || (name.StartsWith("archive", StringComparison.OrdinalIgnoreCase)
                                   && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                               || (name.StartsWith("archive", StringComparison.OrdinalIgnoreCase)
                                   && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                               || (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                   && name.Length > 12 && char.IsDigit(name[0]) && char.IsDigit(name[1]));
                if (!managed) continue;
                File.Move(file, Path.Combine(target, name), overwrite: true);
            }

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

    /// <summary>
    /// 把指定条目的图片打包导出为 zip 压缩包（与内部归档无关）。
    /// 返回实际导出的条数（图片文件已丢失的条目跳过）；失败抛异常，由调用方提示。
    /// </summary>
    public int ExportZip(IReadOnlyList<HistoryEntry> entries, string zipPath)
    {
        int exported = 0;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var e in entries)
            {
                var src = Path.Combine(_currentDir, e.FileName);
                if (!File.Exists(src)) continue;
                zip.CreateEntryFromFile(src, e.FileName, CompressionLevel.Optimal);
                exported++;
            }
        }
        return exported;
    }

    // ---------------- 归档（按月压缩包） ----------------

    /// <summary>把搜索范围覆盖到的归档月份解压到活跃列表（幂等：同月只解压一次）。
    /// 数据文件落地当前目录，条目按时间合入并标记 FromArchive；压缩包保留。</summary>
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
                Program.LogError("归档解压", ex);
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
            TryDeleteFile(e);
        Entries = Entries.Where(e => !e.FromArchive).ToList();
        Save();
        Changed?.Invoke();
    }

    /// <summary>超过活跃上限后先攒缓冲条数，攒满一次性归档：原生条目达到 limit+缓冲 时，
    /// 把超出 limit 的最旧条目按截图月份追加进对应 archive_YYYY-MM.zip。
    /// 解压条目（FromArchive）不参与归档，等待清除搜索时丢弃。</summary>
    private void ArchiveOverflow()
    {
        int limit = _config.Config.History.ActiveLimit;
        if (limit <= 0) return; // 0 = 永不归档

        int buffer = Math.Max(0, _config.Config.History.ArchiveBuffer);
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
                Program.LogError("历史归档", ex);
            }
        }
        Entries = Entries.Where(e => !archivedIds.Contains(e.Id)).ToList();
    }

    /// <summary>把一批条目追加进指定月份的压缩包（含 meta.json 元数据合并；同名数据幂等覆盖）</summary>
    private void AppendToMonthArchive(DateTime month, List<HistoryEntry> entries)
    {
        var path = MonthArchivePath(month);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var meta = ReadMeta(zip);

        foreach (var e in entries)
        {
            var src = Path.Combine(_currentDir, e.FileName);
            if (!File.Exists(src)) continue;
            zip.GetEntry(e.FileName)?.Delete(); // 幂等：同名旧条目覆盖
            zip.CreateEntryFromFile(src, e.FileName, CompressionLevel.Optimal);
            File.Delete(src);
        }

        var ids = entries.Select(e => e.Id).ToHashSet();
        meta.RemoveAll(m => ids.Contains(m.Id));
        meta.AddRange(entries.Select(e => new HistoryEntry
        {
            Id = e.Id,
            FileName = e.FileName,
            CreatedAt = e.CreatedAt,
            Width = e.Width,
            Height = e.Height,
            SavedPath = null,
            Action = e.Action,
            FromArchive = false,
        }));
        WriteMeta(zip, meta);
    }

    /// <summary>从条目所在月份的压缩包移除该条目（单条删除解压条目时同步，避免再搜回来）</summary>
    private void RemoveFromArchive(HistoryEntry entry)
    {
        try
        {
            var path = MonthArchivePath(entry.CreatedAt);
            if (!File.Exists(path)) return;
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            zip.GetEntry(entry.FileName)?.Delete();
            var meta = ReadMeta(zip);
            if (meta.RemoveAll(m => m.Id == entry.Id) > 0)
                ArchivedCount--;
            WriteMeta(zip, meta);
        }
        catch (Exception ex)
        {
            Program.LogError("归档条目移除", ex);
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
    private static List<HistoryEntry> ReadMeta(ZipArchive zip)
    {
        var entry = zip.GetEntry(MetaEntryName);
        if (entry == null) return new List<HistoryEntry>();
        try
        {
            using var sr = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<List<HistoryEntry>>(sr.ReadToEnd())
                   ?? new List<HistoryEntry>();
        }
        catch
        {
            return new List<HistoryEntry>();
        }
    }

    /// <summary>重写月包内 meta.json</summary>
    private static void WriteMeta(ZipArchive zip, List<HistoryEntry> meta)
    {
        zip.GetEntry(MetaEntryName)?.Delete();
        var entry = zip.CreateEntry(MetaEntryName);
        using var sw = new StreamWriter(entry.Open());
        sw.Write(JsonSerializer.Serialize(meta, JsonOptions));
    }

    /// <summary>旧版单包 archive.zip（文件名 yyyyMMdd_HHmmss_Id.png，无月份划分）一次性迁移为月包。
    /// 迁移后删除旧包与旧元数据 archive.json。</summary>
    private void MigrateLegacyArchive()
    {
        try
        {
            if (!File.Exists(LegacyArchivePath)) return;
            using var zip = ZipFile.OpenRead(LegacyArchivePath);
            var groups = new Dictionary<DateTime, List<(string Name, HistoryEntry Entry)>>();
            foreach (var entry in zip.Entries)
            {
                var name = entry.Name;
                if (name.Length == 0 || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                // 旧命名 yyyyMMdd_HHmmss_{Id}.png：前 15 位为可排序时间戳
                if (name.Length < 16 || !DateTime.TryParseExact(name[..15], "yyyyMMdd_HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var t))
                    continue;
                var id = name.Length > 22 ? name[16..^4] : Guid.NewGuid().ToString("N");
                var e = new HistoryEntry
                {
                    Id = id,
                    FileName = name,
                    CreatedAt = t,
                    Action = "",
                };
                var month = new DateTime(t.Year, t.Month, 1);
                if (!groups.TryGetValue(month, out var list)) groups[month] = list = new();
                list.Add((name, e));
            }

            foreach (var (month, items) in groups)
            {
                var dest = MonthArchivePath(month);
                using var target = ZipFile.Open(dest, ZipArchiveMode.Update);
                var meta = ReadMeta(target);
                foreach (var (name, e) in items)
                {
                    var src = zip.GetEntry(name);
                    if (src == null) continue;
                    target.GetEntry(name)?.Delete();
                    var newEntry = target.CreateEntry(name, CompressionLevel.Optimal);
                    using (var os = newEntry.Open())
                    using (var rs = src.Open())
                        rs.CopyTo(os); // 复制原始字节（旧包只读）
                    if (meta.All(m => m.Id != e.Id)) meta.Add(e);
                }
                WriteMeta(target, meta);
            }
        }
        catch (Exception ex)
        {
            Program.LogError("旧归档迁移", ex);
            return; // 迁移失败保留旧包，下次再试
        }

        TryDelete(LegacyArchivePath);
        TryDelete(LegacyArchiveMetaFile);
    }

    /// <summary>从全部月包 meta.json 统计归档条数（旧 archive.json 计数废弃）</summary>
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
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(
                    File.ReadAllText(IndexFile)) ?? new List<HistoryEntry>();
                // 过滤掉图片丢失的条目；解压标记与月份记录在索引保存时自然维持
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

    private void TryDeleteFile(HistoryEntry entry)
    {
        try { File.Delete(Path.Combine(_currentDir, entry.FileName)); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
