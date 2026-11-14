using Android.App;
using Android.Content;
using Android.OS;
using System;

namespace BiliDownloader.Android;

/// <summary>
/// 下载期间的前台服务：常驻通知 + 进程保活，避免下载被系统回收。
/// </summary>
[Service(Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class DownloadForegroundService : Service
{
    private const string ChannelId = "downloads";
    private const string ExtraText = "text";
    private const int NotificationId = 1001;

    private static bool _started;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannel();
        StartForeground(NotificationId, BuildNotification(intent?.GetStringExtra(ExtraText) ?? "正在下载"));
        _started = true;
        return StartCommandResult.Sticky;
    }

    public static void Start(string text)
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(DownloadForegroundService));
        intent.PutExtra(ExtraText, text);

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);

            _started = true;
        }
        catch
        {
            // 前台服务不可用时不影响下载本身
        }
    }

    /// <summary>更新通知文案（下载进度等）。</summary>
    public static void Notify(string text)
    {
        if (!_started) return;

        try
        {
            var context = global::Android.App.Application.Context;
            var manager = (NotificationManager?)context.GetSystemService(NotificationService);
            manager?.Notify(NotificationId, BuildNotification(text));
        }
        catch
        {
            // 通知更新失败可忽略
        }
    }

    public static void Stop()
    {
        if (!_started) return;

        try
        {
            var context = global::Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(DownloadForegroundService)));
        }
        catch
        {
            // 忽略
        }

        _started = false;
    }

    private static void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

        var context = global::Android.App.Application.Context;
        var manager = (NotificationManager?)context.GetSystemService(NotificationService);
        if (manager == null || manager.GetNotificationChannel(ChannelId) != null) return;

        var channel = new NotificationChannel(ChannelId, "下载任务", NotificationImportance.Low)
        {
            Description = "显示下载进度，保持下载不中断"
        };
        manager.CreateNotificationChannel(channel);
    }

    private static Notification BuildNotification(string text)
    {
        var context = global::Android.App.Application.Context;
        EnsureChannel();

#pragma warning disable CS0618
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(context, ChannelId)
            : new Notification.Builder(context);
#pragma warning restore CS0618

        var contentIntent = PendingIntent.GetActivity(context, 0,
            new Intent(context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        builder
            .SetContentTitle("B站视频下载器")
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetContentIntent(contentIntent)
            .SetOngoing(true);

        return builder.Build();
    }
}
