using Android.App;
using Android.Content;
using Android.OS;
using System;
using System.Threading.Tasks;

namespace BiliDownloader.Android;

/// <summary>
/// 调起系统文件选择器挑一张图片，返回图片 URI。
/// </summary>
[Activity(Theme = "@style/MyTheme.NoActionBar", Exported = false, NoHistory = true)]
public class ImagePickerActivity : Activity
{
    private const int RequestCode = 1002;

    private static TaskCompletionSource<string?>? _pending;

    public static Task<string?> PickAsync()
    {
        if (_pending is { Task.IsCompleted: false }) return _pending.Task;

        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(ImagePickerActivity));
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
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.SetType("image/*");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
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
                ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch
            {
                // 本次会话外不可访问时忽略
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
