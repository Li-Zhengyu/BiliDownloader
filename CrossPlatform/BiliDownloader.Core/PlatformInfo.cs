namespace BiliDownloader;

using System.IO;

/// <summary>跨平台路径与运行环境抽象，解决 Android/Desktop 差异</summary>
public static class PlatformInfo
{
    public static bool IsAndroid => OperatingSystem.IsAndroid();
    public static bool IsDesktop => !OperatingSystem.IsAndroid();

    /// <summary>Android 端由 MainActivity 注入的应用专属下载目录（外部存储，无需申请权限）</summary>
    public static string? AndroidOutputDirOverride { get; set; }

    /// <summary>Android 端由 MainActivity 注入的应用缓存目录</summary>
    public static string? AndroidTempDirOverride { get; set; }

    /// <summary>Android 端由 MainActivity 注入的应用数据目录（配置、ffmpeg）</summary>
    public static string? AndroidDataDirOverride { get; set; }

    /// <summary>本地数据目录（配置文件、ffmpeg 等存放处）</summary>
    public static string LocalDataDir =>
        IsAndroid && !string.IsNullOrEmpty(AndroidDataDirOverride)
            ? AndroidDataDirOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDownloader");

    /// <summary>默认下载输出目录</summary>
    public static string DefaultOutputDir
    {
        get
        {
            if (IsAndroid)
            {
                // Android: 优先使用 MainActivity 注入的应用专属目录
                if (!string.IsNullOrEmpty(AndroidOutputDirOverride))
                {
                    TryCreate(AndroidOutputDirOverride);
                    return AndroidOutputDirOverride;
                }
                // 回退：外部存储 Download 目录，再回退到应用私有目录
                var download = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Download");
                return Directory.Exists(download) || TryCreate(download)
                    ? Path.Combine(download, "BiliDownloader")
                    : Path.Combine(LocalDataDir, "Downloads");
            }
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return string.IsNullOrEmpty(videos)
                ? Path.Combine(LocalDataDir, "Downloads")
                : Path.Combine(videos, "BiliDownloader");
        }
    }

    /// <summary>临时文件目录</summary>
    public static string TempDir =>
        IsAndroid && !string.IsNullOrEmpty(AndroidTempDirOverride)
            ? AndroidTempDirOverride
            : Path.Combine(Path.GetTempPath(), "BiliDownloader");

    /// <summary>日志保存目录：Android 使用应用数据目录（临时目录可能不可写），桌面使用系统临时目录</summary>
    private static string LogDir => IsAndroid ? LocalDataDir : Path.GetTempPath();

    /// <summary>日志文件路径</summary>
    public static string LogPath => Path.Combine(LogDir, "BiliDownloader_dbg.log");

    /// <summary>崩溃日志路径</summary>
    public static string CrashLogPath => Path.Combine(LogDir, "BiliDownloader_crash.log");

    private static bool TryCreate(string dir)
    {
        try { Directory.CreateDirectory(dir); return true; } catch { return false; }
    }
}
