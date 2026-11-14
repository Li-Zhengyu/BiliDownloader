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
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDownloader", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var exe = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(exe)) return exe;
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
            // 不重定向输出：避免管道缓冲区满导致ffmpeg阻塞死锁
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(30)); }
        catch (TimeoutException) { try { p.Kill(); } catch { } return false; }   // 超时保护
        return p.ExitCode == 0 && File.Exists(outFile);
    }

    /// <summary>把 AAC 音频流转码为指定格式（需要 ffmpeg），codecArgs 为编码参数</summary>
    public static async Task<bool> ConvertAudio(string input, string output, string codecArgs)
    {
        var ff = FindFfmpeg();
        if (ff == null) return false;
        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = $"-y -i \"{input}\" {codecArgs} \"{output}\"",
            UseShellExecute = false, CreateNoWindow = true
            // 不重定向输出：转码进度会灌满stderr管道导致死锁，改为丢弃输出
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(60)); }   // 超时保护（长音频转码较慢）
        catch (TimeoutException) { try { p.Kill(); } catch { } return false; }
        var ok = p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        if (!ok) { try { if (File.Exists(output)) File.Delete(output); } catch { } }   // 清理0字节残留文件
        return ok;
    }

    public static bool MergeConcat(string v, string a, string outFile)
    {
        // 简单拼接（部分播放器可播放，作为最后兜底）
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

    // 串行化：多个并行任务只允许一个真正下载，其余复用结果
    private static readonly SemaphoreSlim FfGate = new(1, 1);
    private static bool _ffAttempted;
    private static string? _ffResultPath;
    private static string? _ffResultError;

    /// <summary>下载并安装 ffmpeg，返回 (路径, 错误信息)。全局只下载一次，其余任务复用</summary>
    public static async Task<(string? Path, string? Error)> DownloadFfmpeg()
    {
        // 已可用直接返回
        if (FindFfmpeg() is { } existing) return (existing, null);
        if (_ffAttempted) return (_ffResultPath, _ffResultError);
        await FfGate.WaitAsync();   // 异步信号量：只有一个任务真正下载，其余等待后复用
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
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDownloader", "ffmpeg");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "ffmpeg.exe");
        var zip = Path.Combine(Path.GetTempPath(), $"ffmpeg_tmp_{Environment.ProcessId}.zip");

        // 多下载源：国内镜像（快）→ 官方源 → 代理 → GitHub，逐个尝试
        var sources = new[]
        {
            // 兰州大学镜像（Jellyfin ffmpeg，实测 21MB/s）
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
                c.Timeout = System.Threading.Timeout.InfiniteTimeSpan;   // ★ 大文件不能超时，慢速也要下完
                c.DefaultRequestHeaders.UserAgent.ParseAdd("BiliDownloader/1.0");
                using var resp = await c.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                await using (var fs = new FileStream(zip, FileMode.Create))
                    await resp.Content.CopyToAsync(fs);

                var size = new FileInfo(zip).Length;
                if (size < 1_000_000)   // 过小说明下载错误/被拦截
                {
                    lastErr = $"下载的文件过小({size / 1024 / 1024:F1}MB)，源不可用";
                    BiliClient.Log($"ffmpeg文件过小: {size}");
                    continue;
                }

                BiliClient.Log($"ffmpeg下载完成 {size / 1024 / 1024:F1}MB，解压中...");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, true);
                var exe = Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe != null)
                {
                    if (exe != dest) File.Copy(exe, dest, true);
                    try { File.Delete(zip); } catch { }
                    if (FfWorks(dest))   // ★ 验证 exe 真能运行，避免损坏文件
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
                    lastErr = "解压后未找到 ffmpeg.exe";
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

    /// <summary>验证 ffmpeg.exe 能否正常运行</summary>
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
