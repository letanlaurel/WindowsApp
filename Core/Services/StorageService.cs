using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using SnipPin.Core.Configuration;

namespace SnipPin.Core.Services;

/// <summary>存储服务：保存文件 + 写剪贴板</summary>
public class StorageService : IStorageService
{
    private readonly ConfigService _config;

    public StorageService(ConfigService config)
    {
        _config = config;
    }

    public string SaveToFile(BitmapSource image)
    {
        var save = _config.Config.Save;
        var dir = save.Directory;
        Directory.CreateDirectory(dir);

        var format = save.Format.ToLowerInvariant();
        var ext = format switch
        {
            "jpg" or "jpeg" => "jpg",
            "bmp" => "bmp",
            _ => "png",
        };

        // 生成文件名（替换命名模板中的占位符）
        var now = DateTime.Now;
        var name = save.NameTemplate
            .Replace("{yyyy-MM-dd}", now.ToString("yyyy-MM-dd"))
            .Replace("{HH-mm-ss}", now.ToString("HH-mm-ss"));
        var path = Path.Combine(dir, $"{name}.{ext}");

        // 同名冲突时追加序号
        int seq = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, $"{name}_{seq++}.{ext}");

        BitmapEncoder encoder = ext switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = save.JpgQuality },
            "bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
        return path;
    }

    public void CopyToClipboard(BitmapSource image)
    {
        Clipboard.SetImage(image);
    }
}
