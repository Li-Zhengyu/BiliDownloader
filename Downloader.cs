namespace BiliDownloader;

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

public class Downloader
{
    private readonly HttpClient _http;
    public int Retries { get; set; }   // 失败重试次数

    public Downloader(string? refererPage = null)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None, MaxConnectionsPerServer = 16 };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", BiliClient.UserAgent);
        // Referer：优先具体视频页 URL（yt-dlp 做法，防盗链更稳），否则用根域
        _http.DefaultRequestHeaders.Add("Referer", refererPage ?? "https://www.bilibili.com");
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;  // 大文件下载不能有短超时
        // ★ 关键修复：CDN 下载必须带 Cookie（匿名 buvid3 + 登录 SESSDATA）
        _http.DefaultRequestHeaders.Add("Cookie", BiliClient.CurrentCookie);
        Retries = Config.Current.RetryCount;
    }

    /// <summary>下载文件，自动尝试备用 URL + 失败重试，返回最终文件大小</summary>
    public async Task<long> DownloadAsync(string url, string outputPath, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default,
        IEnumerable<string>? backupUrls = null)
    {
        var urls = new List<string> { url };
        if (backupUrls != null) urls.AddRange(backupUrls);

        Exception? lastErr = null;
        for (int attempt = 0; attempt <= Retries; attempt++)
        {
            if (attempt > 0)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1500 * attempt, ct);   // 递增退避后重试
            }
            foreach (var u in urls)
            {
                try
                {
                    var size = await DownloadSingleAsync(u, outputPath, progress, ct);
                    if (size > 1024) return size;   // 有效文件（>1KB）
                    lastErr = new Exception($"文件太小({size}字节)，可能URL无效");
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                }
                catch (OperationCanceledException) { throw; }   // 暂停/取消不重试
                catch (Exception ex)
                {
                    lastErr = ex;
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                }
            }
        }
        throw lastErr ?? new Exception("下载失败");
    }

    private async Task<long> DownloadSingleAsync(string url, string outputPath, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        if (total > 0) progress?.Report((0, total));   // ★ 先报告真实总大小，让进度条有基准
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

        var buf = new byte[65536];
        long done = 0;
        int read;
        var lastReport = DateTime.MinValue;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if ((DateTime.Now - lastReport).TotalMilliseconds > 150)
            {
                lastReport = DateTime.Now;
                progress?.Report((done, total));
            }
        }
        progress?.Report((done, total));
        return done;
    }

    public static string FormatMB(long bytes) => $"{bytes / 1024.0 / 1024.0:F1}MB";
}
