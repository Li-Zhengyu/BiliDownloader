using Android.App;
using Android.Content;
using Android.OS;
using System;
using System.Threading.Tasks;

namespace BiliDownloader.Android;

/// <summary>
/// 调起系统文件夹选择器（SAF），返回用户选择的目录 URI。
/// </summary>
[Activity(Theme = "@style/MyTheme.NoActionBar", Exported = false, NoHistory = true)]
public class FolderPickerActivity : Activity
{
    private const int RequestCode = 1001;

    private static TaskCompletionSource<string?>? _pending;

    public static Task<string?> PickAsync()
    {
        if (_pending is { Task.IsCompleted: false }) return _pending.Task;

        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(FolderPickerActivity));
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

        try
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission
                            | ActivityFlags.GrantWriteUriPermission
                            | ActivityFlags.GrantPersistableUriPermission
                            | ActivityFlags.GrantPrefixUriPermission);
            StartActivityForResult(intent, RequestCode);
        }
        catch (Exception ex)
        {
            _pending?.TrySetException(ex);
            Finish();
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == RequestCode && resultCode == Result.Ok && data?.Data != null)
        {
            var uri = data.Data!;
            try
            {
                ContentResolver?.TakePersistableUriPermission(uri,
                    ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            }
            catch
            {
                // 部分系统不允许持久化，仅本次会话可用
            }

            _pending?.TrySetResult(uri.ToString());
        }
        else
        {
            _pending?.TrySetResult(null);
        }

        Finish();
    }

    protected override void OnDestroy()
    {
        _pending?.TrySetResult(null);
        base.OnDestroy();
    }
}
