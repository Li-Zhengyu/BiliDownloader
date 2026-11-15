using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using System;

namespace BiliDownloader.Android;

/// <summary>
/// 下载进度悬浮窗：常驻在其它应用上方，显示实时进度，同时提升进程优先级，
/// 避免国产系统在后台把下载进程杀掉（需要「显示在其他应用上层」权限）。
/// </summary>
internal static class DownloadOverlay
{
    private static IWindowManager? _wm;
    private static View? _panel;
    private static TextView? _txtTitle;
    private static TextView? _txtInfo;
    private static ProgressBar? _bar;

    public static bool IsShowing => _panel != null;

    public static bool HasPermission()
    {
        try
        {
            return Build.VERSION.SdkInt < BuildVersionCodes.M
                   || Settings.CanDrawOverlays(global::Android.App.Application.Context);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>跳到系统的「显示在其他应用上层」授权页。</summary>
    public static void RequestPermission()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var intent = new Intent(Settings.ActionManageOverlayPermission,
                global::Android.Net.Uri.Parse("package:" + ctx.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(intent);
        }
        catch (Exception ex)
        {
            CrashReporter.Write("打开悬浮窗权限设置失败", ex);
        }
    }

    public static void Show(string info, int percent)
    {
        if (!HasPermission()) return;
        if (_panel != null) { Update(info, percent); return; }

        try
        {
            var ctx = global::Android.App.Application.Context;
            _wm = ctx.GetSystemService(Context.WindowService)?.JavaCast<IWindowManager>();
            if (_wm == null) return;

            var density = ctx.Resources?.DisplayMetrics?.Density ?? 1f;
            int Dp(int v) => (int)(v * density + 0.5f);

            var panel = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            panel.SetPadding(Dp(14), Dp(10), Dp(14), Dp(12));
            panel.SetMinimumWidth(Dp(220));

            var bg = new GradientDrawable();
            bg.SetColor(Color.ParseColor("#F21F2024"));
            bg.SetCornerRadius(Dp(14));
            bg.SetStroke(Dp(1), Color.ParseColor("#33FFFFFF"));
            panel.Background = bg;

            var header = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            _txtTitle = new TextView(ctx) { TextSize = 13f, Text = "B站视频下载器" };
            _txtTitle.SetTextColor(Color.White);
            _txtTitle.LayoutParameters = new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WrapContent, 1f);
            header.AddView(_txtTitle);

            var btnClose = new TextView(ctx) { Text = "✕", TextSize = 15f };
            btnClose.SetTextColor(Color.ParseColor("#B0FFFFFF"));
            btnClose.SetPadding(Dp(10), 0, 0, 0);
            btnClose.Click += (_, _) => Hide();
            header.AddView(btnClose);
            panel.AddView(header);

            _txtInfo = new TextView(ctx) { TextSize = 12f, Text = info };
            _txtInfo.SetTextColor(Color.ParseColor("#CCFFFFFF"));
            var infoLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            infoLp.TopMargin = Dp(2);
            panel.AddView(_txtInfo, infoLp);

#pragma warning disable CS0618
            _bar = new ProgressBar(ctx, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                Max = 100,
                Progress = Math.Clamp(percent, 0, 100)
            };
#pragma warning restore CS0618
            _bar.Indeterminate = percent <= 0;
            var barLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(6));
            barLp.TopMargin = Dp(8);
            panel.AddView(_bar, barLp);

            var type = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? WindowManagerTypes.ApplicationOverlay
                : (WindowManagerTypes)2002;
            var lp = new WindowManagerLayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent,
                type,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen | WindowManagerFlags.NotTouchModal,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Start,
                X = Dp(16),
                Y = Dp(96)
            };

            // 可拖动，避免挡住操作
            int startX = 0, startY = 0;
            float downX = 0, downY = 0;
            bool dragging = false;
            panel.Touch += (_, e) =>
            {
                var motion = e.Event;
                if (motion == null) return;
                switch (motion.Action)
                {
                    case MotionEventActions.Down:
                        startX = lp.X;
                        startY = lp.Y;
                        downX = motion.RawX;
                        downY = motion.RawY;
                        dragging = false;
                        break;
                    case MotionEventActions.Move:
                        var dx = (int)(motion.RawX - downX);
                        var dy = (int)(motion.RawY - downY);
                        if (Math.Abs(dx) > Dp(4) || Math.Abs(dy) > Dp(4)) dragging = true;
                        lp.X = startX + dx;
                        lp.Y = startY + dy;
                        try { _wm?.UpdateViewLayout(panel, lp); } catch { }
                        break;
                    case MotionEventActions.Up:
                        if (dragging) e.Handled = true;
                        break;
                }
            };

            _wm.AddView(panel, lp);
            _panel = panel;
            Update(info, percent);
        }
        catch (Exception ex)
        {
            CrashReporter.Write("显示下载浮窗失败", ex);
            Hide();
        }
    }

    public static void Update(string info, int percent)
    {
        if (_panel == null) return;

        new Handler(Looper.MainLooper!).Post(() =>
        {
            try
            {
                if (_txtTitle != null) _txtTitle.Text = $"下载中 {Math.Clamp(percent, 0, 100)}%";
                if (_txtInfo != null) _txtInfo.Text = info;
                if (_bar != null)
                {
                    _bar.Indeterminate = percent <= 0;
                    _bar.Progress = Math.Clamp(percent, 0, 100);
                }
            }
            catch
            {
                // 界面已销毁时忽略
            }
        });
    }

    public static void Hide()
    {
        new Handler(Looper.MainLooper!).Post(() =>
        {
            try
            {
                if (_panel != null && _wm != null) _wm.RemoveView(_panel);
            }
            catch
            {
                // 忽略
            }
            finally
            {
                _panel = null;
                _wm = null;
                _txtTitle = null;
                _txtInfo = null;
                _bar = null;
            }
        });
    }
}
