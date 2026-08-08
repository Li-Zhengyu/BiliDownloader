using System.IO;
using System.Windows;

namespace BiliDownloader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var log = Path.Combine(Path.GetTempPath(), "BiliDownloader_crash.log");
        DispatcherUnhandledException += (s, args) =>
        {
            try { File.WriteAllText(log, args.Exception.ToString()); } catch { }
            args.Handled = true;
        };
        Config.Load();
        base.OnStartup(e);
    }
}
