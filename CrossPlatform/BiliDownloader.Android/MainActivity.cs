using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using BiliDownloader;
using System;
using System.IO;
using System.Threading.Tasks;

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
            Instance = this;
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
            // 必须继续抛出：吞掉异常会让系统在 onCreate 结束时抛 SuperNotCalledException，定位信息全丢
            throw;
        }
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
        base.OnDestroy();
    }

    /// <summary>当前存活的 MainActivity，供 Avalonia 界面发起系统对话框。</summary>
    public static MainActivity? Instance { get; private set; }

    private const int FolderRequestCode = 1002;

    private static TaskCompletionSource<string?>? _folderTask;

    /// <summary>
    /// 由 Activity 自己发起系统目录选择（不能放到独立任务里，否则返回时重建 Activity 会崩）。
    /// </summary>
    public static Task<string?> PickFolderAsync()
    {
        var activity = Instance;
        if (activity == null) return Task.FromResult<string?>(null);
        if (_folderTask is { Task.IsCompleted: false }) return _folderTask.Task;

        _folderTask = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            try
            {
                var intent = new Intent(Intent.ActionOpenDocumentTree);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission
                                | ActivityFlags.GrantWriteUriPermission
                                | ActivityFlags.GrantPersistableUriPermission
                                | ActivityFlags.GrantPrefixUriPermission);
                activity.StartActivityForResult(intent, FolderRequestCode);
            }
            catch (Exception ex)
            {
                CrashReporter.Write("打开目录选择器失败", ex);
                _folderTask?.TrySetResult(null);
                _folderTask = null;
            }
        });

        return _folderTask.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != FolderRequestCode) return;

        string? uri = null;
        try
        {
            if (resultCode == Result.Ok && data?.Data != null)
            {
                var picked = data.Data!;
                uri = picked.ToString();
                try
                {
                    ContentResolver?.TakePersistableUriPermission(picked,
                        ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                }
                catch
                {
                    // 部分系统不允许持久化，仅本次会话可用
                }
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Write("处理目录选择结果失败", ex);
        }

        _folderTask?.TrySetResult(uri);
        _folderTask = null;
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
