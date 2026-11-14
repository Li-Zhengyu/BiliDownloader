using Avalonia;
using Avalonia.Markup.Xaml;
using System.IO;

namespace BiliDownloader.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 崩溃日志
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.WriteAllText(PlatformInfo.CrashLogPath, e.ExceptionObject?.ToString() ?? ""); } catch { }
        };
        Config.Load();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
