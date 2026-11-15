using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace BiliDownloader.Android;

public partial class MobileApp : Avalonia.Application
{
    public override void Initialize()
    {
        CrashReporter.Write("MobileApp.Initialize 开始", null);

        // 先确定主题变体再加载样式：避免加载完再切换变体导致整套主题资源重建
        ThemeStore.Load();
        ThemeStore.Apply();

        // 手机端没有 ffmpeg：注册系统 MediaMuxer 合流，保证只输出可直接播放的 MP4
        DownloadProcessor.CustomMuxer = AndroidMp4Muxer.TryMux;

        try
        {
            AvaloniaXamlLoader.Load(this);
            CrashReporter.Write("MobileApp.Initialize 完成", null);
        }
        catch (Exception ex)
        {
            CrashReporter.Write("MobileApp.Initialize AvaloniaXamlLoader.Load 失败", ex);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Android 使用单视图生命周期（ISingleViewApplicationLifetime）
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            try
            {
                singleView.MainView = new MobileMainView();
                CrashReporter.Write("MobileMainView 创建成功", null);
            }
            catch (Exception ex)
            {
                CrashReporter.Write("MobileMainView 创建失败", ex);
                singleView.MainView = BuildFallbackView(ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Control BuildFallbackView(Exception ex)
    {
        return new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = "界面初始化失败，详细信息已写入 crash.log：\n\n" + ex,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
                Foreground = Brushes.White,
            },
            Background = Brush.Parse("#161616"),
        };
    }
}
