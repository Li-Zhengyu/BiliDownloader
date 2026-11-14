namespace BiliDownloader;

using System.IO;

public class VideoInfo
{
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public string Title { get; set; } = "";
    public string Pic { get; set; } = "";
    public int Duration { get; set; }
    public string OwnerName { get; set; } = "";
    public long OwnerMid { get; set; }
    public List<VideoPage> Pages { get; set; } = [];
    // UGC 合集（多集）
    public long UgcSeasonId { get; set; }
    public string UgcSeasonTitle { get; set; } = "";
    public List<VideoPage> Series { get; set; } = [];
}

public class VideoPage
{
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public long Cid { get; set; }
    public int Page { get; set; }
    public string Part { get; set; } = "";
    public int Duration { get; set; }
}

public class DashStream
{
    public int Id { get; set; }
    public string BaseUrl { get; set; } = "";
    public List<string> BackupUrl { get; set; } = [];
    public string Codecs { get; set; } = "";
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class QualityInfo
{
    public int Quality { get; set; }
    public string Name { get; set; } = "";
    public bool NeedVip { get; set; }
}

public class PlayUrlData
{
    public List<DashStream> VideoStreams { get; set; } = [];
    public List<DashStream> AudioStreams { get; set; } = [];
    public List<QualityInfo> Qualities { get; set; } = [];
    public string? DurlUrl { get; set; }
    public long DurlSize { get; set; }
    public int ActualQuality { get; set; }
}

public class SubtitleInfo
{
    public string Lan { get; set; } = "";
    public string LanDoc { get; set; } = "";
    public string SubtitleUrl { get; set; } = "";
}

public class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Bvid { get; set; } = "";
    public long Cid { get; set; }
    public string Title { get; set; } = "";
    public string PartTitle { get; set; } = "";
    public DownloadMode Mode { get; set; } = DownloadMode.Video;
    public string OutputDir { get; set; } = "";
    public string? OutputFile { get; set; }
    public string Status { get; set; } = "等待中";
    public string StepText { get; set; } = "等待中";
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedMBs { get; set; }
    public string? Error { get; set; }
    public System.Threading.CancellationTokenSource? Cts { get; set; }
    public string AudioFormat { get; set; } = "mp3";
    /// <summary>优先下载单文件 MP4（手机端无 ffmpeg 时避免音视频无法合并）</summary>
    public bool PreferSingleFile { get; set; }

    public string DisplayTitle => string.IsNullOrEmpty(PartTitle) ? Title : $"{Title} - {PartTitle}";
}

public enum DownloadMode
{
    Video, AudioOnly, SubtitleOnly, Package
}

public class LoginInfo
{
    public string? Sessdata { get; set; }
    public string? BiliJct { get; set; }
    public string? DedeUserId { get; set; }
    public string? UserName { get; set; }
    public bool IsLoggedIn => !string.IsNullOrEmpty(Sessdata);
}

public class AppConfig
{
    public string OutputDir { get; set; } = PlatformInfo.DefaultOutputDir;
    public LoginInfo? Login { get; set; }
    public string? FfmpegPath { get; set; }
    public string CacheDir { get; set; } = "";
    public int RetryCount { get; set; } = 3;
    public int MaxParallel { get; set; } = 3;
    public string AudioFormat { get; set; } = "mp3";
}
