using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using BiliDownloader;
using System;
using System.IO;

namespace BiliDownloader.Android;

[Activity(
    Label = "B站视频下载器",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustPan,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
[IntentFilter(new[] { global::Android.Content.Intent.ActionSend },
    Categories = new[] { global::Android.Content.Intent.CategoryDefault },
    DataMimeType = "text/plain")]
public class MainActivity : AvaloniaMainActivity<MobileApp>
{
    static MainActivity()
    {
        // 静态构造在 Activity 创建之前执行，尽早安装崩溃记录（含 Java 未捕获异常）
        try
        {
            CrashReporter.Install(ResolveExternalLogDirectory());
            InstallJavaCrashHandler();
            CrashReporter.WriteText("MainActivity 类已加载", null);
        }
        catch
        {
            // 忽略：日志不可用时也要保证应用能启动
        }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        ConfigureAndroidPaths();
        CrashReporter.Install(PlatformInfo.AndroidDataDirOverride);
        CrashReporter.WriteText("MainActivity.CustomizeAppBuilder 开始", null);

        try
        {
            Config.Load();
            CrashReporter.WriteText("Config.Load 完成", null);
        }
        catch (Exception ex)
        {
            CrashReporter.Write("Config.Load 失败", ex);
        }

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        try
        {
            CrashReporter.WriteText("MainActivity.OnCreate 开始", null);
            base.OnCreate(savedInstanceState);
            ApplyScreenOrientation();
            HandleShareIntent(Intent);
            RequestNotificationPermission();
            CrashReporter.WriteText("MainActivity.OnCreate 完成", null);
        }
        catch (Exception ex)
        {
            // 启动阶段异常不要静默闪退：写日志并弹窗提示，方便截图反馈
            CrashReporter.Write("MainActivity.OnCreate 失败", ex);
            ShowStartupError(ex);
        }
    }

    /// <summary>Android 13+ 需要用户授权才能显示前台服务通知。</summary>
    private void RequestNotificationPermission()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 200);
        }
        catch
        {
            // 拒绝授权不影响下载
        }
    }

    protected override void OnNewIntent(global::Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleShareIntent(intent);
    }

    /// <summary>接收 B站 客户端「分享」过来的文本，提取链接交给主界面自动解析。</summary>
    private void HandleShareIntent(global::Android.Content.Intent? intent)
    {
        try
        {
            if (intent?.Action != global::Android.Content.Intent.ActionSend) return;

            var text = intent.GetStringExtra(global::Android.Content.Intent.ExtraText);
            if (string.IsNullOrWhiteSpace(text)) return;

            SharedLink.Set(text);
            CrashReporter.WriteText("收到分享文本", text);
        }
        catch (Exception ex)
        {
            CrashReporter.Write("处理分享失败", ex);
        }
    }

    private void ApplyScreenOrientation()
    {
        // 平板（最小宽度 >= 600dp）跟随重力自由旋转；手机锁定竖屏
        var smallestWidth = Resources?.Configuration?.SmallestScreenWidthDp ?? 0;
        RequestedOrientation = smallestWidth >= 600
            ? ScreenOrientation.User
            : ScreenOrientation.Portrait;
    }

    private void ShowStartupError(Exception ex)
    {
        try
        {
            RunOnUiThread(() =>
            {
                try
                {
                    var builder = new AlertDialog.Builder(this);
                    builder.SetTitle("启动失败");
                    builder.SetMessage(ex.GetType().Name + ": " + ex.Message
                                       + "\n\n日志已保存到手机「下载/BiliDownloader」目录，也可查看：" + (CrashReporter.LogPath ?? "不可用"));
                    builder.SetPositiveButton("退出", (_, _) => Finish());
                    builder.Show();
                }
                catch
                {
                    // 弹窗失败时忽略
                }
            });
        }
        catch
        {
            // 忽略
        }
    }

    private static string? ResolveExternalLogDirectory()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var baseDir = context?.GetExternalFilesDir(null)?.AbsolutePath
                          ?? context?.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(baseDir))
                return null;

            var dir = Path.Combine(baseDir!, "BiliDownloader");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return null;
        }
    }

    private static void InstallJavaCrashHandler()
    {
        try
        {
            var previous = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
            Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaCrashHandler(previous);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// Android 10+ 受分区存储限制，直接写公共 Download 目录需要额外权限；
    /// 这里使用应用专属外部目录，无需运行时权限，用户可通过文件管理器访问。
    /// </summary>
    private void ConfigureAndroidPaths()
    {
        var baseDir = GetExternalFilesDir(null)?.AbsolutePath
                      ?? FilesDir?.AbsolutePath
                      ?? CacheDir?.AbsolutePath;

        if (!string.IsNullOrEmpty(baseDir))
        {
            var outputDir = Path.Combine(baseDir!, "Download");
            try { Directory.CreateDirectory(outputDir); } catch { }
            PlatformInfo.AndroidOutputDirOverride = outputDir;
        }

        if (!string.IsNullOrEmpty(FilesDir?.AbsolutePath))
        {
            var dataDir = Path.Combine(FilesDir!.AbsolutePath, "BiliDownloader");
            try { Directory.CreateDirectory(dataDir); } catch { }
            PlatformInfo.AndroidDataDirOverride = dataDir;
        }

        if (!string.IsNullOrEmpty(CacheDir?.AbsolutePath))
            PlatformInfo.AndroidTempDirOverride = CacheDir!.AbsolutePath;
    }

    private sealed class JavaCrashHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _previous;

        public JavaCrashHandler(Java.Lang.Thread.IUncaughtExceptionHandler? previous) => _previous = previous;

        public void UncaughtException(Java.Lang.Thread? t, Java.Lang.Throwable? e)
        {
            CrashReporter.WriteText("Java.UncaughtException", e?.ToString(), true);

            if (_previous != null)
            {
                try { _previous.UncaughtException(t!, e!); } catch { }
            }
            else
            {
                Java.Lang.JavaSystem.Exit(1);
            }
        }
    }
}
