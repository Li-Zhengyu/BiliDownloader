using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Runtime;

namespace BiliDownloader.Android;

/// <summary>
/// 手机端启动/运行期崩溃记录器。
/// - 常规日志写到应用专属目录 Android/data/com.bilidownloader.app/files/BiliDownloader/crash.log
/// - 发生异常时，额外把一份日志写到公共“下载”目录（Download/BiliDownloader/），
///   这样即使没有 adb，也能用文件管理器直接取出来。
/// </summary>
internal static class CrashReporter
{
    private static bool _installed;
    private static string? _logPath;
    private static readonly ConcurrentQueue<string> _pending = new();
    private static readonly object _fileLock = new();
    private static int _flushing;

    public static string? LogPath => _logPath;

    public static void Install(string? logDirectory)
    {
        if (!string.IsNullOrEmpty(logDirectory))
        {
            try
            {
                Directory.CreateDirectory(logDirectory!);
                _logPath = Path.Combine(logDirectory!, "crash.log");
            }
            catch
            {
                _logPath = null;
            }
        }

        if (_installed)
            return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteText("AppDomain.UnhandledException", e.ExceptionObject?.ToString(), true);

        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            WriteText("AndroidEnvironment.UnhandledExceptionRaiser", e.Exception?.ToString(), true);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
    }

    /// <summary>记录一个阶段标记或托管异常（ex 为 null 时只写阶段名）。</summary>
    public static void Write(string stage, Exception? ex)
        => WriteText(stage, ex?.ToString(), ex != null);

    /// <summary>记录一个阶段标记或任意文本。</summary>
    public static void WriteText(string stage, string? detail, bool crash = false)
    {
        string content;
        try
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ").Append(stage);
            if (!string.IsNullOrEmpty(detail))
                sb.AppendLine().Append(detail);
            sb.AppendLine();
            content = sb.ToString();
        }
        catch
        {
            return;
        }

        if (crash)
        {
            // 崩溃必须落盘：先把排队中的日志同步写掉，再写本次异常
            lock (_fileLock)
            {
                while (_pending.TryDequeue(out var queued)) AppendFile(queued);
                AppendFile(content);
            }
            WritePublicCopy(content);
            return;
        }

        // 普通启动日志走后台队列，避免启动过程中同步写外部存储拖慢冷启动
        _pending.Enqueue(content);
        StartFlush();
    }

    private static void StartFlush()
    {
        if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0) return;
        _ = Task.Run(FlushLoop);
    }

    private static void FlushLoop()
    {
        try
        {
            while (_pending.TryDequeue(out var queued)) AppendFile(queued);
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
            if (!_pending.IsEmpty) StartFlush();
        }
    }

    private static void AppendFile(string content)
    {
        try
        {
            if (_logPath != null) File.AppendAllText(_logPath, content);
        }
        catch
        {
            // 忽略写日志失败
        }
    }

    /// <summary>把日志写到公共下载目录（Android 10+ 走 MediaStore，无需存储权限）。</summary>
    private static void WritePublicCopy(string content)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var resolver = context?.ContentResolver;
            if (context == null || resolver == null)
                return;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var values = new ContentValues();
                values.Put(MediaStore.IMediaColumns.DisplayName,
                    $"BiliDownloader-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                values.Put(MediaStore.IMediaColumns.MimeType, "text/plain");
                values.Put(MediaStore.IMediaColumns.RelativePath, "Download/BiliDownloader");

                var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values);
                if (uri == null)
                    return;

                using var stream = resolver.OpenOutputStream(uri);
                if (stream == null)
                    return;
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            }
        }
        catch
        {
            // 写公共目录失败时忽略（仍有应用专属目录日志）
        }
    }
}
