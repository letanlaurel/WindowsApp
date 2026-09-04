using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SnipPin.Core.Configuration;
using SnipPin.Core.Services;

namespace SnipPin;

/// <summary>
/// 应用入口：单实例 + Generic Host（DI 容器）承载所有后台服务。
/// 手动 new Application() 以便接入 DI。
/// </summary>
public static class Program
{
    // 单实例互斥体名称（带会话前缀避免跨用户冲突）
    private const string MutexName = @"Local\TLSnipPin_SingleInstance";

    [STAThread]
    private static int Main()
    {
        // 从旧目录 SnipPin 一次性迁移到 TLSnipPin（保留历史与配置）
        MigrateOldDataDir();

        // 单实例检测：已有实例则直接退出
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // TODO: 向已运行实例发送消息让其弹出截图，此处先直接退出
            return 0;
        }

        var app = new Application();

        // 全局异常兜底：写入错误日志；UI 线程异常拦截后保持常驻（不再闪退）
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => LogError("AppDomain", e.ExceptionObject);
        app.DispatcherUnhandledException += (_, e) =>
        {
            LogError("Dispatcher", e.Exception);
            e.Handled = true;
        };

        // 构建 Host：统一注册配置、日志、服务
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((ctx, services) =>
            {
                // 配置（单例，可保存）
                services.AddSingleton(ConfigService.Load());
                // 应用主窗体（隐藏，仅承载消息泵与托盘）
                services.AddSingleton<MainWindow>();
                // 业务服务
                services.AddSingleton<IHotkeyService, HotkeyService>();
                services.AddSingleton<ICaptureService, CaptureService>();
                services.AddSingleton<IPinService, PinService>();
                services.AddSingleton<IStorageService, StorageService>();
                services.AddSingleton<HistoryService>();
            })
            .Build();

        // 应用退出时保存配置并释放 Host
        app.Exit += (_, _) =>
        {
            host.Services.GetRequiredService<ConfigService>().Save();
            host.Dispose();
        };

        app.Startup += (_, _) =>
        {
            // 手动创建主窗体并注入服务
            var main = host.Services.GetRequiredService<MainWindow>();
            main.Init(host.Services);
            app.MainWindow = main;
            // 主窗体默认隐藏，仅托盘常驻
            main.Hide();
        };

        return app.Run();
    }

    /// <summary>应用数据目录（%AppData%\TLSnipPin）</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TLSnipPin");

    /// <summary>
    /// 旧版数据目录（%AppData%\SnipPin）→ 新目录一次性迁移。
    /// 仅当新目录不存在且旧目录存在时执行，保留全部配置/历史/会话数据。
    /// </summary>
    private static void MigrateOldDataDir()
    {
        try
        {
            var oldDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipPin");
            if (Directory.Exists(oldDir) && !Directory.Exists(DataDir))
                Directory.Move(oldDir, DataDir);
        }
        catch
        {
            // 迁移失败不阻断启动（新目录将重新初始化）
        }
    }

    /// <summary>追加写入错误日志（%AppData%\TLSnipPin\error.log）</summary>
    public static void LogError(string source, object? exception)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(
                Path.Combine(DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败时静默（不能因日志再抛异常）
        }
    }

    /// <summary>
    /// 加载应用图标（WPF ImageSource，优先从进程 exe 提取，失败回退嵌入资源）。
    /// 单文件发布下 pack:// 解析不稳定时，exe 图标仍可靠（任务栏图标即用此）。
    /// </summary>
    public static System.Windows.Media.ImageSource? LoadAppIcon()
    {
        // 首选：从当前进程 exe 提取关联图标并转为 ImageSource
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
        }
        catch { /* 落到资源加载 */ }

        // 退路：从嵌入资源读取
        try
        {
            var sri = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/logo.ico"));
            if (sri == null) return null;
            using var stream = sri.Stream;
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            // 取最大尺寸的帧
            return decoder.Frames
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
