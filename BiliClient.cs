namespace BiliDownloader;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public class BiliApiException : Exception
{
    public int Code { get; }
    public BiliApiException(int code, string msg) : base(msg) { Code = code; }
}

public static class BiliClient
{
    private static readonly HttpClient _http;
    private static readonly string _buvid3;

    static BiliClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, MaxConnectionsPerServer = 16 };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com/");
        _http.DefaultRequestHeaders.Add("Origin", "https://www.bilibili.com");
        _http.Timeout = TimeSpan.FromSeconds(30);

        var bytes = new byte[16];
        new Random().NextBytes(bytes);
        _buvid3 = Convert.ToHexString(bytes) + "infoc";
        _http.DefaultRequestHeaders.Add("Cookie", $"buvid3={_buvid3}");
    }

    public static bool IsLoggedIn => Config.Current.Login?.IsLoggedIn == true;
    public static HttpClient SharedHttpClient => _http;
    public static string Referer => "https://www.bilibili.com/";
    public static string UserAgent => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>当前 Cookie 字符串（供下载器复用，CDN 必需）</summary>
    public static string CurrentCookie
    {
        get
        {
            var login = Config.Current.Login;
            if (login?.Sessdata != null)
                return $"buvid3={_buvid3}; SESSDATA={login.Sessdata}; bili_jct={login.BiliJct ?? ""}; DedeUserID={login.DedeUserId ?? ""}";
            return $"buvid3={_buvid3}";
        }
    }

    public static void ApplyLoginCookie()
    {
        _http.DefaultRequestHeaders.Remove("Cookie");
        var login = Config.Current.Login;
        if (login?.Sessdata == null)
        {
            _http.DefaultRequestHeaders.Add("Cookie", $"buvid3={_buvid3}");
            return;
        }
        _http.DefaultRequestHeaders.Add("Cookie",
            $"buvid3={_buvid3}; SESSDATA={login.Sessdata}; bili_jct={login.BiliJct ?? ""}; DedeUserID={login.DedeUserId ?? ""}");
    }

    public static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BiliDownloader_dbg.log"),
                $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    private static async Task<JsonElement> BiliGet(string url, bool useLogin = true)
    {
        if (useLogin && IsLoggedIn) ApplyLoginCookie();
        var resp = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var c) && c.GetInt32() != 0)
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "错误";
            Log($"API错误 url={url} code={c.GetInt32()} msg={msg}");
            throw new BiliApiException(c.GetInt32(), msg ?? "");
        }
        return root.GetProperty("data").Clone();
    }

    public static async Task<VideoInfo> GetVideoInfo(string bvid)
    {
        var data = await BiliGet($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}");
        var info = new VideoInfo
        {
            Bvid = data.GetProperty("bvid").GetString() ?? bvid,
            Title = data.GetProperty("title").GetString() ?? "",
            Pic = data.GetProperty("pic").GetString() ?? "",
            Duration = data.GetProperty("duration").GetInt32(),
            OwnerName = data.GetProperty("owner").GetProperty("name").GetString() ?? "",
            OwnerMid = data.GetProperty("owner").TryGetProperty("mid", out var mid) ? mid.GetInt64() : 0
        };
        foreach (var p in data.GetProperty("pages").EnumerateArray())
        {
            info.Pages.Add(new VideoPage
            {
                Bvid = info.Bvid,
                Cid = p.GetProperty("cid").GetInt64(),
                Page = p.GetProperty("page").GetInt32(),
                Part = p.GetProperty("part").GetString() ?? $"P{p.GetProperty("page").GetInt32()}",
                Duration = p.GetProperty("duration").GetInt32()
            });
        }
        // UGC 合集：view 接口返回 ugc_season，再拉全量分集列表
        if (data.TryGetProperty("ugc_season", out var us) && us.ValueKind == JsonValueKind.Object)
        {
            info.UgcSeasonId = us.TryGetProperty("id", out var sid) ? sid.GetInt64() : 0;
            info.UgcSeasonTitle = us.TryGetProperty("title", out var st) ? st.GetString() ?? "" : "";
            if (info.UgcSeasonId > 0)
            {
                try { info.Series = await LoadSeries(info.OwnerMid, info.UgcSeasonId); }
                catch (Exception ex) { Log($"加载合集列表异常: {ex.Message}"); }
                if (info.Series.Count == 0)
                {
                    // 回退：view 接口自带的 ugc_season.sections（含 cid，可能不完整）
                    info.Series = ParseSeriesFromView(us);
                    Log($"合集回退view.sections: {info.Series.Count}集");
                }
                // 无论来源，补齐缺失的 cid
                await FixMissingCid(info.Series);
                Log($"合集: id={info.UgcSeasonId} 标题={info.UgcSeasonTitle} 共{info.Series.Count}集");
            }
        }
        return info;
    }

    private static async Task<List<VideoPage>> LoadSeries(long mid, long seasonId)
    {
        var list = new List<VideoPage>();
        int pn = 1, maxPages = 10;
        while (pn <= maxPages)
        {
            var data = await BiliGet($"https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?mid={mid}&season_id={seasonId}&page_num={pn}&page_size=30");
            if (data.TryGetProperty("archives", out var arcs) && arcs.ValueKind == JsonValueKind.Array)
            {
                int idx = (pn - 1) * 30;
                foreach (var a in arcs.EnumerateArray())
                {
                    list.Add(new VideoPage
                    {
                        Bvid = a.TryGetProperty("bvid", out var b) ? b.GetString() ?? "" : "",
                        Aid = a.TryGetProperty("aid", out var aid) ? aid.GetInt64() : 0,
                        Cid = a.TryGetProperty("cid", out var c) ? c.GetInt64() : 0,
                        Page = ++idx,
                        Part = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Duration = a.TryGetProperty("duration", out var d) ? d.GetInt32() : 0
                    });
                }
            }
            // 分页：用 page.total / page_size 计算总页数（has_more 字段并不可靠）
            if (data.TryGetProperty("page", out var pg))
            {
                var total = pg.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                var ps = pg.TryGetProperty("page_size", out var pss) ? pss.GetInt32() : 30;
                if (total > 0)
                    maxPages = Math.Min(50, (int)Math.Ceiling(total / (double)(ps > 0 ? ps : 30)));
            }
            pn++;
        }
        return list;
    }

    /// <summary>从 view 接口的 ugc_season.sections 解析分集（兜底，可能不完整）</summary>
    private static List<VideoPage> ParseSeriesFromView(JsonElement ugcSeason)
    {
        var list = new List<VideoPage>();
        int idx = 0;
        if (ugcSeason.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
            foreach (var sec in sections.EnumerateArray())
                if (sec.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
                    foreach (var e in eps.EnumerateArray())
                    {
                        long dur = 0;
                        if (e.TryGetProperty("arc", out var arc) && arc.TryGetProperty("duration", out var d)) dur = d.GetInt32();
                        list.Add(new VideoPage
                        {
                            Bvid = e.TryGetProperty("bvid", out var b) ? b.GetString() ?? "" : "",
                            Aid = e.TryGetProperty("aid", out var aid) ? aid.GetInt64() : 0,
                            Cid = e.TryGetProperty("cid", out var c) ? c.GetInt64() : 0,
                            Page = ++idx,
                            Part = e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                            Duration = (int)dur
                        });
                    }
        return list;
    }

    /// <summary>缺 cid 的视频用 view 接口并发补齐（seasons_archives_list 常不返回 cid）</summary>
    private static async Task FixMissingCid(List<VideoPage> list)
    {
        var missing = list.Where(x => x.Cid == 0).ToList();
        if (missing.Count == 0) return;
        Log($"补齐 {missing.Count} 个视频的 cid...");
        using var sem = new SemaphoreSlim(8);
        await Task.WhenAll(missing.Select(async v =>
        {
            await sem.WaitAsync();
            try
            {
                var url = !string.IsNullOrEmpty(v.Bvid)
                    ? $"https://api.bilibili.com/x/web-interface/view?bvid={v.Bvid}"
                    : $"https://api.bilibili.com/x/web-interface/view?aid={v.Aid}";
                var data = await BiliGet(url);
                if (string.IsNullOrEmpty(v.Bvid)) v.Bvid = data.TryGetProperty("bvid", out var b) ? b.GetString() ?? "" : "";
                v.Cid = data.TryGetProperty("cid", out var c) ? c.GetInt64() : 0;
            }
            catch { }
            finally { sem.Release(); }
        }));
        int still0 = list.Count(x => x.Cid == 0);
        if (still0 > 0) Log($"仍有 {still0} 个视频无 cid");
    }

    public static (string Type, string Id) ParseUrl(string url)
    {
        var bv = Regex.Match(url, @"BV[a-zA-Z0-9]{10}", RegexOptions.IgnoreCase);
        if (bv.Success) return ("video", bv.Value);
        var av = Regex.Match(url, @"av(\d+)", RegexOptions.IgnoreCase);
        if (av.Success) return ("video", av.Groups[1].Value);
        return ("unknown", "");
    }

    /// <summary>
    /// 解析分享文本里的链接：先按文本里的 BV/av 号直接识别；
    /// 识别不到时（如 b23.tv 短链）请求一次并跟随 302，从真实地址/页面里提取 BV 号。
    /// </summary>
    public static async Task<(string Type, string Id)> ParseUrlAsync(string text)
    {
        var direct = ParseUrl(text);
        if (direct.Type != "unknown") return direct;

        var shortUrl = Regex.Match(text, @"https?://[^\s""'<>]+").Value;
        if (string.IsNullOrEmpty(shortUrl)) return direct;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, shortUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var byUrl = ParseUrl(finalUrl);
            if (byUrl.Type != "unknown") return byUrl;

            // 少数短链跳转后地址里仍没有 BV，再从页面内容里找
            var html = await resp.Content.ReadAsStringAsync();
            return ParseUrl(html);
        }
        catch
        {
            return ("unknown", "");
        }
    }

    public static async Task<PlayUrlData> GetPlayUrl(string bvid, long cid, int qn = 32, bool audioOnly = false)
    {
        var result = new PlayUrlData();

        // 仅音频：优先直接拿 DASH 独立音频流（比下载整个视频更小更快）
        if (audioOnly)
        {
            var tmp = new PlayUrlData();
            if (await TryDashAsync(tmp, bvid, cid, qn) && tmp.AudioStreams.Count > 0)
            {
                Log($"GetPlayUrl 仅音频: DASH音频流 {tmp.AudioStreams.Count} 个");
                return tmp;
            }
            Log("仅音频DASH失败，回退完整视频");
        }

        // 登录且高画质 → 先试 DASH
        if (IsLoggedIn && qn > 64)
        {
            if (await TryDashAsync(result, bvid, cid, qn)) return result;
        }

        // durl 单文件（免登录可靠路径）
        try
        {
            Log($"GetPlayUrl durl: bvid={bvid} cid={cid} qn={qn}");
            var data = await BiliGet($"https://api.bilibili.com/x/player/playurl?bvid={bvid}&cid={cid}&qn={qn}&fnval=0&fourk=1");
            if (data.TryGetProperty("accept_quality", out var aq) && aq.ValueKind == JsonValueKind.Array)
            {
                var names = data.TryGetProperty("accept_description", out var ad) ? ad : default;
                int i = 0;
                foreach (var q in aq.EnumerateArray())
                {
                    var name = names.ValueKind == JsonValueKind.Array && i < names.GetArrayLength()
                        ? names[i].GetString() ?? $"{q.GetInt32()}P" : $"{q.GetInt32()}P";
                    result.Qualities.Add(new QualityInfo { Quality = q.GetInt32(), Name = name, NeedVip = q.GetInt32() >= 112 });
                    i++;
                }
            }
            if (data.TryGetProperty("durl", out var durl) && durl.GetArrayLength() > 0)
            {
                var d = durl[0];
                // B站 durl 的 url 字段可能含换行符或多个URL用 \n 分隔，需清洗
                var rawUrl = d.GetProperty("url").GetString() ?? "";
                var urlList = rawUrl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                result.DurlUrl = urlList.FirstOrDefault(u => u.StartsWith("http")) ?? urlList.FirstOrDefault();
                result.DurlSize = d.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                result.ActualQuality = data.TryGetProperty("quality", out var q) ? q.GetInt32() : qn;
            }
        }
        catch (Exception ex) { Log($"durl失败: {ex.Message}"); }

        if (string.IsNullOrEmpty(result.DurlUrl) && result.VideoStreams.Count == 0)
        {
            Log("durl为空，尝试DASH");
            await TryDashAsync(result, bvid, cid, qn);
        }

        if (string.IsNullOrEmpty(result.DurlUrl) && result.VideoStreams.Count == 0)
            Log("GetPlayUrl 返回空！");

        return result;
    }

    private static async Task<bool> TryDashAsync(PlayUrlData result, string bvid, long cid, int qn)
    {
        try
        {
            var tryLook = IsLoggedIn ? "" : "&try_look=1";   // try_look 仅免登录
            var baseUrl = $"https://api.bilibili.com/x/player/wbi/playurl?bvid={bvid}&cid={cid}&qn={qn}&fnval=4048&fourk=1{tryLook}";
            var data = await BiliGet(await SignWbiAsync(baseUrl));
            result.ActualQuality = data.TryGetProperty("quality", out var q) ? q.GetInt32() : qn;
            if (data.TryGetProperty("dash", out var dash))
            {
                if (dash.TryGetProperty("video", out var v))
                    foreach (var x in v.EnumerateArray())
                        result.VideoStreams.Add(ParseDash(x));
                if (dash.TryGetProperty("audio", out var a))
                    foreach (var x in a.EnumerateArray())
                        result.AudioStreams.Add(ParseDash(x));
                return result.VideoStreams.Count > 0 && result.AudioStreams.Count > 0;
            }
        }
        catch { }
        return false;
    }

    private static DashStream ParseDash(JsonElement e)
    {
        return new DashStream
        {
            Id = e.GetProperty("id").GetInt32(),
            BaseUrl = e.GetProperty("baseUrl").GetString() ?? "",
            BackupUrl = e.TryGetProperty("backupUrl", out var bu) && bu.ValueKind == JsonValueKind.Array
                ? bu.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
            Codecs = e.TryGetProperty("codecs", out var c) ? c.GetString() ?? "" : "",
            Size = e.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
            Width = e.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            Height = e.TryGetProperty("height", out var h) ? h.GetInt32() : 0
        };
    }

    // ============ 登录 ============

    public static async Task<(string Key, string Url)> GenQr()
    {
        var data = await BiliGet("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", useLogin: false);
        return (data.GetProperty("qrcode_key").GetString() ?? "", data.GetProperty("url").GetString() ?? "");
    }

    public static async Task<(int Code, string? Redirect)> PollQr(string key)
    {
        var data = await BiliGet($"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={key}", useLogin: false);
        return (data.GetProperty("code").GetInt32(), data.TryGetProperty("url", out var u) ? u.GetString() : null);
    }

    public static async Task<LoginInfo?> GetLogin(string redirect)
    {
        // B站 poll 返回的 url 是 crossDomain 链接，query 参数里直接带 SESSDATA/DedeUserID/bili_jct
        string? s = null, j = null, d = null;
        try
        {
            var uri = new Uri(redirect);
            var pairs = uri.Query.TrimStart('?').Split('&')
                .Select(x => x.Split('=', 2))
                .Where(x => x.Length == 2)
                .ToDictionary(x => Uri.UnescapeDataString(x[0]),
                              x => Uri.UnescapeDataString(x[1]),
                              StringComparer.OrdinalIgnoreCase);
            s = pairs.GetValueOrDefault("SESSDATA");
            j = pairs.GetValueOrDefault("bili_jct");
            d = pairs.GetValueOrDefault("DedeUserID");
            Log($"GetLogin query: SESSDATA={(string.IsNullOrEmpty(s) ? "空" : "有")} bili_jct={(string.IsNullOrEmpty(j) ? "空" : "有")} DedeUserID={(string.IsNullOrEmpty(d) ? "空" : "有")}");
        }
        catch (Exception ex) { Log($"GetLogin 解析URL失败: {ex.Message}"); }

        // 兜底：若 URL 参数没有，访问 redirect 从 Set-Cookie 提取
        if (string.IsNullOrEmpty(s))
        {
            try
            {
                var cookies = new System.Net.CookieContainer();
                var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true };
                using var c = new HttpClient(handler);
                c.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                c.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com/");
                await c.GetAsync(redirect);
                foreach (System.Net.Cookie ck in cookies.GetAllCookies())
                {
                    if (ck.Name == "SESSDATA" && string.IsNullOrEmpty(s)) s = ck.Value;
                    else if (ck.Name == "bili_jct" && string.IsNullOrEmpty(j)) j = ck.Value;
                    else if (ck.Name == "DedeUserID" && string.IsNullOrEmpty(d)) d = ck.Value;
                }
                Log($"GetLogin cookie兜底: SESSDATA={(string.IsNullOrEmpty(s) ? "空" : "有")}");
            }
            catch (Exception ex) { Log($"GetLogin cookie兜底失败: {ex.Message}"); }
        }

        if (string.IsNullOrEmpty(s)) { Log("GetLogin 最终无SESSDATA，登录失败"); return null; }

        // 拿用户名
        string? name = null;
        try
        {
            using var c2 = new HttpClient();
            c2.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            c2.DefaultRequestHeaders.Add("Cookie", $"SESSDATA={s}");
            var json = await c2.GetStringAsync("https://api.bilibili.com/x/web-interface/nav");
            using var doc = JsonDocument.Parse(json);
            name = doc.RootElement.GetProperty("data").GetProperty("uname").GetString();
        }
        catch { }

        Log("GetLogin 成功");
        return new LoginInfo { Sessdata = s, BiliJct = j, DedeUserId = d, UserName = name };
    }

    public static LoginInfo? ParseCookie(string raw)
    {
        var d = raw.Split(';').Select(x => x.Trim().Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim(), x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var sess = d.GetValueOrDefault("SESSDATA");
        if (string.IsNullOrEmpty(sess)) return null;
        return new LoginInfo
        {
            Sessdata = sess,
            BiliJct = d.GetValueOrDefault("bili_jct"),
            DedeUserId = d.GetValueOrDefault("DedeUserID")
        };
    }

    // ============ 字幕 ============

    public static async Task<List<SubtitleInfo>> GetSubtitles(string bvid, long cid)
    {
        var list = new List<SubtitleInfo>();
        try
        {
            var data = await BiliGet($"https://api.bilibili.com/x/player/wbi/v2?bvid={bvid}&cid={cid}");
            if (data.TryGetProperty("subtitle", out var sub) && sub.TryGetProperty("subtitles", out var subs))
            {
                foreach (var s in subs.EnumerateArray())
                {
                    var url = s.GetProperty("subtitle_url").GetString() ?? "";
                    if (url.StartsWith("//")) url = "https:" + url;
                    list.Add(new SubtitleInfo
                    {
                        Lan = s.GetProperty("lan").GetString() ?? "",
                        LanDoc = s.GetProperty("lan_doc").GetString() ?? "",
                        SubtitleUrl = url
                    });
                }
            }
        }
        catch { }
        return list;
    }

    public static async Task<string> DownloadSubtitle(string url)
        => await _http.GetStringAsync(url);

    // ============ wbi 签名（yt-dlp / BBDownT 交叉验证） ============

    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    };
    private static string? _mixinKey;
    private static DateTime _keyExpire = DateTime.MinValue;

    private static async Task<string> GetMixinKey()
    {
        if (_mixinKey != null && DateTime.UtcNow < _keyExpire) return _mixinKey;
        try
        {
            var json = await _http.GetStringAsync("https://api.bilibili.com/x/web-interface/nav");
            using var doc = JsonDocument.Parse(json);
            var d = doc.RootElement.GetProperty("data");
            var imgUrl = d.GetProperty("wbi_img").GetProperty("img_url").GetString();
            var subUrl = d.GetProperty("wbi_img").GetProperty("sub_url").GetString();
            if (string.IsNullOrEmpty(imgUrl) || string.IsNullOrEmpty(subUrl)) { _mixinKey = ""; return _mixinKey; }
            var img = Path.GetFileNameWithoutExtension(new Uri(imgUrl).AbsolutePath);
            var sub = Path.GetFileNameWithoutExtension(new Uri(subUrl).AbsolutePath);
            var lookup = img + sub;
            if (lookup.Length < 64) { _mixinKey = ""; return _mixinKey; }   // wbi key 需至少 64 字符
            var key = new char[32];
            for (int i = 0; i < 32; i++) key[i] = lookup[MixinKeyEncTab[i]];
            _mixinKey = new string(key);
            _keyExpire = DateTime.UtcNow.AddSeconds(30);
        }
        catch { _mixinKey = ""; }
        return _mixinKey;
    }

    private static async Task<string> SignWbiAsync(string url)
    {
        var mixinKey = await GetMixinKey();
        if (string.IsNullOrEmpty(mixinKey)) return url;

        var uri = new Uri(url);
        var pairs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var query = uri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = kv.IndexOf('=');
                if (i <= 0) continue;
                var k = System.Net.WebUtility.UrlDecode(kv[..i]);
                var v = System.Net.WebUtility.UrlDecode(kv[(i + 1)..]);
                pairs[k] = v;
            }
        }

        var wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        pairs["wts"] = wts;

        var raw = string.Join("&", pairs.Select(p =>
            $"{System.Net.WebUtility.UrlEncode(p.Key)}={System.Net.WebUtility.UrlEncode(p.Value)}"));
        var wrid = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(raw + mixinKey)))
            .ToLowerInvariant();

        var parts = new List<string> { $"{System.Net.WebUtility.UrlEncode("wts")}={System.Net.WebUtility.UrlEncode(wts)}" };
        foreach (var p in pairs)
            if (p.Key != "wts")
                parts.Add($"{System.Net.WebUtility.UrlEncode(p.Key)}={System.Net.WebUtility.UrlEncode(p.Value)}");
        parts.Add($"w_rid={wrid}");

        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}?{string.Join("&", parts)}";
    }

    /// <summary>筛选稳定流：跳过 PCDN（带非默认端口）节点，优先 upos-sz-*.bilivideo.com</summary>
    public static DashStream? PickBestStream(IEnumerable<DashStream> streams, Func<DashStream, bool>? predicate = null)
    {
        var list = predicate == null ? streams.ToList() : streams.Where(predicate).ToList();
        if (list.Count == 0) return null;
        // 判断是否为"带端口的 PCDN 节点"：解析 URL，host 显式带非默认端口视为 PCDN
        static bool IsPcdn(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            return u.Port != 443 && u.Port != 80 && u.Port != -1;
        }
        var best = list.FirstOrDefault(s => !IsPcdn(s.BaseUrl) && s.BaseUrl.Contains("upos-sz"))
                ?? list.FirstOrDefault(s => !IsPcdn(s.BaseUrl))
                ?? list.First();
        return best;
    }
}
