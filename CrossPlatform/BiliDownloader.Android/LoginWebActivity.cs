using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using System;
using System.Threading.Tasks;

namespace BiliDownloader.Android;

/// <summary>
/// 应用内 B站 官方网页登录页。登录成功后从 WebView 的 Cookie 中读取 SESSDATA 等凭据。
/// </summary>
[Activity(Label = "登录 B站账号", Theme = "@style/MyTheme.NoActionBar", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public class LoginWebActivity : Activity
{
    private const string LoginUrl = "https://passport.bilibili.com/login";
    private const string CookieUrl = "https://www.bilibili.com";

    private static TaskCompletionSource<string?>? _pending;

    private WebView? _webView;

    /// <summary>打开登录页并等待登录完成，返回完整 Cookie 字符串；用户取消返回 null。</summary>
    public static Task<string?> LoginAsync()
    {
        if (_pending is { Task.IsCompleted: false }) return _pending.Task;

        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(LoginWebActivity));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            _pending.TrySetException(ex);
        }

        return _pending.Task;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _webView = new WebView(this);
        var settings = _webView.Settings;
        if (settings != null)
        {
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;
            settings.SetSupportMultipleWindows(false);
        }

        CookieManager.Instance?.SetAcceptCookie(true);
        _webView.SetWebViewClient(new LoginWebViewClient(this));
        SetContentView(_webView);
        _webView.LoadUrl(LoginUrl);
    }

    protected override void OnResume()
    {
        base.OnResume();
        TryCollectCookie();
    }

    internal void TryCollectCookie()
    {
        var cookie = CookieManager.Instance?.GetCookie(CookieUrl);
        if (string.IsNullOrEmpty(cookie)) return;
        if (cookie.IndexOf("SESSDATA=", StringComparison.OrdinalIgnoreCase) < 0) return;

        Complete(cookie);
    }

    private void Complete(string? cookie)
    {
        CookieManager.Instance?.Flush();
        _pending?.TrySetResult(cookie);
        Finish();
    }

    public override void OnBackPressed()
    {
        if (_webView?.CanGoBack() == true)
        {
            _webView.GoBack();
            return;
        }

        _pending?.TrySetResult(null);
        base.OnBackPressed();
    }

    protected override void OnDestroy()
    {
        _webView?.Destroy();
        _webView = null;
        _pending?.TrySetResult(null);
        base.OnDestroy();
    }

    private sealed class LoginWebViewClient : WebViewClient
    {
        private readonly LoginWebActivity _owner;

        public LoginWebViewClient(LoginWebActivity owner) => _owner = owner;

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            // 所有跳转都在 WebView 内部完成（包括第三方账号登录页）
            return false;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            _owner.TryCollectCookie();
        }
    }
}
