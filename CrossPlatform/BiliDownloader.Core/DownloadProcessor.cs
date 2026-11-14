namespace BiliDownloader;

using System.IO;

/// <summary>跨平台下载处理器，从 UI 层提取核心下载逻辑，桌面/Android 共用</summary>
public class DownloadProcessor
{
    /// <summary>平台自定义 MP4 合流实现（Android 用系统 MediaMuxer，无需 ffmpeg），参数：视频、音频、输出文件</summary>
    public static Func<string, string, string, bool>? CustomMuxer { get; set; }

    /// <summary>处理单个下载任务，通过 progress 回调报告 UI 更新</summary>
    public static async Task ProcessAsync(DownloadTask task, int qn, IDownloadProgress progress, CancellationToken externalCt = default)
    {
        string? tmpDir = null;
        try
        {
            task.Cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            task.Status = "下载中";
            progress.Refresh();

            var preferSingleFile = task.PreferSingleFile || PlatformInfo.IsAndroid;
            var play = await BiliClient.GetPlayUrl(task.Bvid, task.Cid, qn, task.Mode == DownloadMode.AudioOnly, preferSingleFile);
            task.TotalBytes = play.DurlSize > 0 ? play.DurlSize : 1;
            progress.Refresh();

            // 字幕模式
            if (task.Mode is DownloadMode.SubtitleOnly or DownloadMode.Package)
            {
                var subs = await BiliClient.GetSubtitles(task.Bvid, task.Cid);
                var sub = subs.FirstOrDefault(s => s.Lan.Contains("zh")) ?? subs.FirstOrDefault();
                if (sub == null)
                {
                    if (task.Mode == DownloadMode.SubtitleOnly) { progress.Fail(task, "❌ 无可用字幕"); return; }
                }
                else
                {
                    task.StepText = "下载字幕";
                    progress.Refresh();
                    var json = await BiliClient.DownloadSubtitle(sub.SubtitleUrl);
                    var srt = SubtitleConverter.JsonToSrt(json);
                    var subPath = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.srt");
                    await File.WriteAllTextAsync(subPath, srt);
                    if (task.Mode == DownloadMode.SubtitleOnly)
                    {
                        task.OutputFile = subPath;
                        progress.Finish(task, "✅ 完成");
                        return;
                    }
                }
            }

            // durl 单文件
            if (!string.IsNullOrEmpty(play.DurlUrl))
            {
                task.StepText = "下载视频...";
                progress.Refresh();
                tmpDir = Path.Combine(GetCacheDir(), task.Id);
                Directory.CreateDirectory(tmpDir);
                var tmpFile = Path.Combine(tmpDir, "v.mp4");
                var dl = new Downloader();
                var prog = new Progress<(long Done, long Total)>(p => { task.DownloadedBytes = p.Done; if (p.Total > 0) task.TotalBytes = p.Total; progress.Refresh(); });
                await dl.DownloadAsync(play.DurlUrl, tmpFile, prog, task.Cts.Token);
                var outFile = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.mp4");
                File.Move(tmpFile, outFile, true);
                task.OutputFile = outFile;
                progress.Finish(task, "✅ 完成");
                return;
            }

            // DASH
            var v = BiliClient.PickBestStream(play.VideoStreams, x => x.Id <= qn)
                 ?? BiliClient.PickBestStream(play.VideoStreams);
            var a = BiliClient.PickBestStream(play.AudioStreams);
            if (v == null) { progress.Fail(task, "没有视频流"); return; }
            if (a == null && task.Mode != DownloadMode.AudioOnly) { progress.Fail(task, "未获取到音频流"); return; }

            if (task.Mode == DownloadMode.AudioOnly)
            {
                task.StepText = "下载音频...";
                progress.Refresh();
                tmpDir = Path.Combine(GetCacheDir(), task.Id);
                Directory.CreateDirectory(tmpDir);
                var tmpAF = Path.Combine(tmpDir, "a.m4s");
                task.TotalBytes = a!.Size;
                var dlA = new Downloader($"https://www.bilibili.com/video/{task.Bvid}/");
                await dlA.DownloadAsync(a.BaseUrl, tmpAF, new Progress<(long Done, long Total)>(p => { task.DownloadedBytes = p.Done; if (p.Total > 0) task.TotalBytes = p.Total; progress.Refresh(); }), task.Cts.Token, backupUrls: a.BackupUrl);

                var fmt = string.IsNullOrEmpty(task.AudioFormat) ? "mp3" : task.AudioFormat;
                var outA = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}{AudioExt(fmt)}");

                if (fmt == "m4a")
                {
                    File.Move(tmpAF, outA, true);
                    task.OutputFile = outA;
                    progress.Finish(task, "✅ 完成");
                    return;
                }

                task.StepText = $"转码{fmt.ToUpper()}...";
                progress.Refresh();
                var ok = await Merger.ConvertAudio(tmpAF, outA, AudioCodecArgs(fmt));
                if (!ok)
                {
                    outA = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.m4a");
                    File.Move(tmpAF, outA, true);
                    task.OutputFile = outA;
                    progress.Finish(task, "✅ 完成（无ffmpeg，已存m4a）");
                    return;
                }
                task.OutputFile = outA;
                progress.Finish(task, "✅ 完成");
                return;
            }

            // 并行下音视频
            task.StepText = "下载中...";
            task.TotalBytes = v.Size + a!.Size;
            progress.Refresh();
            tmpDir = Path.Combine(GetCacheDir(), task.Id);
            Directory.CreateDirectory(tmpDir);
            var vPath = Path.Combine(tmpDir, "v.m4s");
            var aPath = Path.Combine(tmpDir, "a.m4s");
            var dl2 = new Downloader($"https://www.bilibili.com/video/{task.Bvid}/");

            long vDone = 0, vTotal = 0, aDone = 0, aTotal = 0;
            var vt = dl2.DownloadAsync(v.BaseUrl, vPath, new Progress<(long Done, long Total)>(p =>
            {
                Interlocked.Exchange(ref vDone, p.Done);
                if (p.Total > 0) Interlocked.Exchange(ref vTotal, p.Total);
                task.DownloadedBytes = Interlocked.Read(ref vDone) + Interlocked.Read(ref aDone);
                task.TotalBytes = Interlocked.Read(ref vTotal) + Interlocked.Read(ref aTotal);
                progress.Refresh();
            }), task.Cts.Token, backupUrls: v.BackupUrl);
            var at = dl2.DownloadAsync(a.BaseUrl, aPath, new Progress<(long Done, long Total)>(p =>
            {
                Interlocked.Exchange(ref aDone, p.Done);
                if (p.Total > 0) Interlocked.Exchange(ref aTotal, p.Total);
                task.DownloadedBytes = Interlocked.Read(ref vDone) + Interlocked.Read(ref aDone);
                task.TotalBytes = Interlocked.Read(ref vTotal) + Interlocked.Read(ref aTotal);
                progress.Refresh();
            }), task.Cts.Token, backupUrls: a.BackupUrl);
            await Task.WhenAll(vt, at);

            // 合并
            task.StepText = "合并中...";
            progress.Refresh();
            var outFile2 = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.mp4");
            var merged = await Merger.MergeWithFfmpeg(vPath, aPath, outFile2);
            if (!merged)
            {
                // 手机端没有 ffmpeg：用系统 MediaMuxer 合成标准 MP4（只输出可直接播放的格式）
                var muxer = CustomMuxer;
                if (muxer != null)
                {
                    task.StepText = PlatformInfo.IsAndroid ? "手机端合流（MediaMuxer）..." : "合流...";
                    progress.Refresh();
                    try { merged = muxer(vPath, aPath, outFile2); }
                    catch (Exception ex) { BiliClient.Log($"自定义合流失败: {ex.Message}"); }
                }
            }
            if (!merged && !PlatformInfo.IsAndroid)
            {
                // 桌面端无 ffmpeg 时用兼容拼接兜底
                task.StepText = "兼容模式拼接...";
                progress.Refresh();
                merged = Merger.MergeConcat(vPath, aPath, outFile2);
            }
            if (!merged)
            {
                if (PlatformInfo.IsAndroid)
                {
                    // 手机端不保留 m4s（无法直接播放），直接失败并提示降低画质
                    try { if (File.Exists(outFile2)) File.Delete(outFile2); } catch { }
                    progress.Fail(task, "❌ 该视频仅提供 DASH 音视频流且合流失败，请降低画质（推荐 1080P 及以下）后重试");
                    return;
                }
                // 桌面端兜底：保留原始 m4s
                File.Move(vPath, Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.v.m4s"), true);
                File.Move(aPath, Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.a.m4s"), true);
            }
            task.OutputFile = merged ? outFile2 : null;
            task.TotalBytes = task.DownloadedBytes;
            progress.Refresh();
            progress.Finish(task, merged ? "✅ 完成" : "⚠ 已保留 m4s（需自行合流）");
        }
        catch (OperationCanceledException)
        {
            if (task.Status == "暂停中")
            {
                task.Status = "已暂停";
                task.StepText = "已暂停";
                progress.Refresh();
                progress.OnPaused();
                return;
            }
            throw;
        }
        catch (Exception ex)
        {
            task.Error = ex.Message;
            progress.Fail(task, $"❌ {ex.Message}");
        }
        finally
        {
            CleanupTmp(tmpDir);
            try { task.Cts?.Dispose(); task.Cts = null; } catch { }
        }
    }

    public static string GetCacheDir()
    {
        var d = Config.Current.CacheDir;
        if (string.IsNullOrEmpty(d)) d = PlatformInfo.TempDir;
        Directory.CreateDirectory(d);
        return d;
    }

    private static void CleanupTmp(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    public static string Sanitize(string name)
    {
        var inv = Path.GetInvalidFileNameChars();
        var c = new string(name.Select(x => inv.Contains(x) ? '_' : x).ToArray());
        return c.Length > 80 ? c[..80] : c;
    }

    public static string AudioExt(string fmt) => fmt switch
    {
        "mp3" => ".mp3", "m4a" => ".m4a", "aac" => ".aac",
        "wav" => ".wav", "flac" => ".flac", "opus" => ".opus",
        _ => ".mp3"
    };

    public static string AudioCodecArgs(string fmt) => fmt switch
    {
        "mp3" => "-c:a libmp3lame -q:a 2 -id3v2_version 3",
        "aac" => "-c:a copy -f adts",
        "wav" => "-c:a pcm_s16le",
        "flac" => "-c:a flac",
        "opus" => "-c:a libopus -b:a 128k",
        _ => "-c:a libmp3lame -q:a 2 -id3v2_version 3"
    };
}

/// <summary>下载进度回调接口，UI 层实现以更新界面</summary>
public interface IDownloadProgress
{
    void Refresh();
    void Finish(DownloadTask task, string step);
    void Fail(DownloadTask task, string msg);
    void OnPaused();
}
