using System;
using System.Text.RegularExpressions;

namespace BiliDownloader.Android;

/// <summary>
/// 承接从 B站 客户端「分享」过来的链接，交给主界面自动解析。
/// </summary>
internal static partial class SharedLink
{
    private static readonly object Gate = new();

    private static string? _pending;

    public static event Action<string>? Received;

    public static void Set(string? sharedText)
    {
        var link = Extract(sharedText);
        if (string.IsNullOrEmpty(link)) return;

        lock (Gate) _pending = link;
        Received?.Invoke(link);
    }

    public static string? Take()
    {
        lock (Gate)
        {
            var link = _pending;
            _pending = null;
            return link;
        }
    }

    /// <summary>从分享文本中提取 B站 链接（b23.tv / bilibili.com）。</summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var matches = LinkRegex().Matches(text);
        foreach (Match m in matches)
        {
            var url = m.Value.TrimEnd('.', ',', '。', '，', ')', '）');
            if (url.Contains("b23.tv", StringComparison.OrdinalIgnoreCase)
                || url.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("bili2233", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        // 没有完整协议头时，尝试直接匹配裸域名
        var bare = BareRegex().Match(text);
        return bare.Success ? "https://" + bare.Value : null;
    }

    [GeneratedRegex(@"https?://[^\s\u3000]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?:b23\.tv|(?:www\.|m\.)?bilibili\.com)/[^\s\u3000]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareRegex();
}
