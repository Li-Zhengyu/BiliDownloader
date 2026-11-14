namespace BiliDownloader;

using System.Diagnostics;
using System.IO;
using System.Net.Http;

public static class Merger
{
    public static string? FindFfmpeg()
    {
        if (!string.IsNullOrEmpty(Config.Current.FfmpegPath) && File.Exists(Config.Current.FfmpegPath))
            return Config.Current.FfmpegPath;
        var local = Path.Combine(PlatformInfo.LocalDataDir, "ffmpeg", PlatformInfo.IsAndroid ? "ffmpeg" : "ffmpeg.exe");
        if (File.Exists(local)) return local;
        // Desktop: 搜索 PATH 环境变量
        if (!PlatformInfo.IsAndroid)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var exe = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    public static async Task<bool> MergeWithFfmpeg(string v, string a, string outFile)
    {
        var ff = FindFfmpeg();
        if (ff == null) return false;
        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = $"-y -i \"{v}\" -i \"{a}\" -c copy -movflags +faststart \"{outFile}\"",
            UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(30)); }
        catch (TimeoutException) { try { p.Kill(); } catch { } return false; }
        return p.ExitCode == 0 && File.Exists(outFile);
    }

    public static async Task<bool> ConvertAudio(string input, string output, string codecArgs)
    {
        var ff = FindFfmpeg();
        if (ff == null) return false;
        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = $"-y -i \"{input}\" {codecArgs} \"{output}\"",
            UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(60)); }
        catch (TimeoutException) { try { p.Kill(); } catch { } return false; }
        var ok = p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        if (!ok) { try { if (File.Exists(output)) File.Delete(output); } catch { } }
        return ok;
    }

    public static bool MergeConcat(string v, string a, string outFile)
    {
        try
        {
            using var outS = new FileStream(outFile, FileMode.Create);
            using var vs = File.OpenRead(v);
            using var as_ = File.OpenRead(a);
            vs.CopyTo(outS);
            as_.CopyTo(outS);
            return true;
        }
        catch { return false; }
    }

    private static readonly SemaphoreSlim FfGate = new(1, 1);
    private static bool _ffAttempted;
    private static string? _ffResultPath;
    private static string? _ffResultError;

    public static async Task<(string? Path, string? Error)> DownloadFfmpeg()
    {
        if (FindFfmpeg() is { } existing) return (existing, null);
        if (_ffAttempted) return (_ffResultPath, _ffResultError);
        await FfGate.WaitAsync();
        try
        {
            if (_ffAttempted) return (_ffResultPath, _ffResultError);
            if (FindFfmpeg() is { } ex2) return (ex2, null);
            _ffAttempted = true;
            var (p, err) = await DownloadFfmpegInner();
            _ffResultPath = p;
            _ffResultError = err;
            return (p, err);
        }
        finally { FfGate.Release(); }
    }

    private static async Task<(string? Path, string? Error)> DownloadFfmpegInner()
    {
        var dir = Path.Combine(PlatformInfo.LocalDataDir, "ffmpeg");
        Directory.CreateDirectory(dir);
        var exeName = PlatformInfo.IsAndroid ? "ffmpeg" : "ffmpeg.exe";
        var dest = Path.Combine(dir, exeName);
        var zip = Path.Combine(Path.GetTempPath(), $"ffmpeg_tmp_{Environment.ProcessId}.zip");

        var sources = new[]
        {
            "https://mirror.lzu.edu.cn/jellyfin/ffmpeg/windows/8.x/8.1.2-2/win64/jellyfin-ffmpeg_8.1.2-2_portable_win64-clang-gpl.zip",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            "https://mirror.ghproxy.com/https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
        };

        string? lastErr = null;
        foreach (var url in sources)
        {
            try
            {
                BiliClient.Log($"下载ffmpeg开始: {url}");
                if (File.Exists(zip)) { try { File.Delete(zip); } catch { } }
                using var c = new HttpClient();
                c.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                c.DefaultRequestHeaders.UserAgent.ParseAdd("BiliDownloader/1.0");
                using var resp = await c.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                await using (var fs = new FileStream(zip, FileMode.Create))
                    await resp.Content.CopyToAsync(fs);

                var size = new FileInfo(zip).Length;
                if (size < 1_000_000)
                {
                    lastErr = $"下载的文件过小({size / 1024 / 1024:F1}MB)，源不可用";
                    BiliClient.Log($"ffmpeg文件过小: {size}");
                    continue;
                }

                BiliClient.Log($"ffmpeg下载完成 {size / 1024 / 1024:F1}MB，解压中...");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, true);
                var exe = Directory.GetFiles(dir, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (exe != null)
                {
                    if (exe != dest) File.Copy(exe, dest, true);
                    try { File.Delete(zip); } catch { }
                    if (FfWorks(dest))
                    {
                        Config.Current.FfmpegPath = dest;
                        Config.Save();
                        BiliClient.Log("ffmpeg 安装成功（已验证可运行）");
                        return (dest, null);
                    }
                    lastErr = "下载的 ffmpeg 无法运行（文件可能损坏）";
                    BiliClient.Log(lastErr);
                }
                else
                {
                    lastErr = "解压后未找到 ffmpeg";
                    BiliClient.Log(lastErr);
                }
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
                BiliClient.Log($"ffmpeg下载失败 {url}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return (null, lastErr ?? "未知错误");
    }

    /// <summary>Android 端：从 APK Assets 提取预置 ffmpeg 二进制到私有目录</summary>
    public static string? ExtractBundledFfmpeg(Func<string, Stream?> assetResolver, Action<string>? setExecutable = null)
    {
        try
        {
            var dir = Path.Combine(PlatformInfo.LocalDataDir, "ffmpeg");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "ffmpeg");
            if (File.Exists(dest) && FfWorks(dest)) return dest;

            using var asset = assetResolver("ffmpeg");
            if (asset == null) return null;
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
            asset.CopyTo(fs);
            // Android 需要设置可执行权限（由调用方通过 Java.IO.File.SetExecutable 实现）
            setExecutable?.Invoke(dest);
            if (FfWorks(dest)) { Config.Current.FfmpegPath = dest; Config.Save(); return dest; }
            return null;
        }
        catch { return null; }
    }

    private static bool FfWorks(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = "-version",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
