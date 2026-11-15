using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BiliDownloader;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace BiliDownloader.Android;

public partial class MobileMainView : UserControl
{
    private readonly ObservableCollection<PageItem> _pages = [];
    private readonly ObservableCollection<PageItem> _series = [];
    private readonly ObservableCollection<QueueItem> _queue = [];
    private bool _initialized;

    public MobileMainView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        Config.Load();
        OutputFolder.Load();
        TxtSaveDir.Text = OutputFolder.IsCustom ? OutputFolder.DisplayName! : "应用内默认目录（点击右侧选择）";

        FillQualities();
        FillOptions();
        FillTheme();

        ListPages.ItemsSource = _pages;
        ListSeries.ItemsSource = _series;
        ListQueue.ItemsSource = _queue;

        ChkAudio_Changed(null, null!);
        UpdateLoginUI();

        SharedLink.Received += OnSharedLinkReceived;
        var pendingLink = SharedLink.Take();
        if (!string.IsNullOrEmpty(pendingLink))
        {
            Dispatcher.UIThread.Post(() => ApplySharedLink(pendingLink));
        }
        UpdateQueueUI();
        _ = LoadBackgroundAsync();

        // 滑动期间暂停背景帧切换，停止后再恢复，保证滚动手感
        MainScroll.ScrollChanged += OnMainScrollChanged;

        // 部分机型启动时会把滚动位置带到中部，这里复位到顶部
        Dispatcher.UIThread.Post(() => MainScroll.Offset = new Vector(0, 0), DispatcherPriority.Loaded);
    }

    private void FillTheme()
    {
        BtnThemeSystem.Classes.Set("primary", ThemeStore.Mode == ThemeStore.System);
        BtnThemeLight.Classes.Set("primary", ThemeStore.Mode == ThemeStore.Light);
        BtnThemeDark.Classes.Set("primary", ThemeStore.Mode == ThemeStore.Dark);
    }

    private void BtnTheme_Click(object? sender, RoutedEventArgs e)
    {
        var mode = sender == BtnThemeLight ? ThemeStore.Light
            : sender == BtnThemeDark ? ThemeStore.Dark
            : ThemeStore.System;

        ThemeStore.Save(mode);
        FillTheme();
    }

    // ==================== 选项初始化 ====================

    private void FillQualities()
    {
        CmbQuality.Items.Clear();
        // 手机端只请求单文件 MP4（durl 最高 1080P），保证产物可直接播放
        AddQuality("1080P", 80);
        AddQuality("720P", 64);
        AddQuality("480P", 32);
        AddQuality("360P", 16);
        CmbQuality.SelectedIndex = 0;
    }

    private void AddQuality(string name, int q)
        => CmbQuality.Items.Add(new ComboBoxItem { Content = name, Tag = q });

    private void FillOptions()
    {
        var parallels = new[] { 1, 2, 3, 4 };
        CmbParallel.Items.Clear();
        foreach (var n in parallels)
            CmbParallel.Items.Add(new ComboBoxItem { Content = $"{n} 个", Tag = n });

        var pi = Array.IndexOf(parallels, Math.Min(4, Math.Max(1, Config.Current.MaxParallel)));
        CmbParallel.SelectedIndex = pi >= 0 ? pi : 1;
        CmbParallel.SelectionChanged += (_, _) =>
        {
            Config.Current.MaxParallel = ParallelCount;
            Config.Save();
        };
    }

    private int ParallelCount
        => CmbParallel.SelectedItem is ComboBoxItem c ? (int)(c.Tag ?? 2) : 2;

    private int SelectedQuality
        => CmbQuality.SelectedItem is ComboBoxItem c ? (int)(c.Tag ?? 32) : 32;

    private void ChkAudio_Changed(object? sender, RoutedEventArgs e)
    {
        var audioOnly = ChkAudio.IsChecked == true;
        ChkSubtitle.IsEnabled = !audioOnly;
        if (audioOnly) ChkSubtitle.IsChecked = false;
    }

    // ==================== 解析 ====================

    private void TxtUrl_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = ParseAsync();
    }

    private void OnSharedLinkReceived(string link) =>
        Dispatcher.UIThread.Post(() => ApplySharedLink(link));

    private void ApplySharedLink(string link)
    {
        TxtUrl.Text = link;
        SetStatus("已获取分享链接，正在解析");
        _ = ParseAsync();
    }

    private async void BtnParse_Click(object? sender, RoutedEventArgs e) => await ParseAsync();

    private async Task ParseAsync()
    {
        var url = TxtUrl.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) { SetStatus("请输入视频链接或 BV 号"); return; }

        var (type, id) = await BiliClient.ParseUrlAsync(url);
        if (type != "video")
        {
            // 短链请求失败时把具体原因留在崩溃日志里，界面上只提示用户粘贴完整链接
            if (!string.IsNullOrEmpty(id)) CrashReporter.WriteText("短链解析失败", id);
            SetStatus("无法识别链接，请粘贴包含 BV 号 / av 号的视频链接");
            return;
        }

        BtnParse.IsEnabled = false;
        BtnParse.Content = "解析中...";
        SetStatus("正在解析视频信息...");
        try
        {
            var video = await BiliClient.GetVideoInfo(id);

            TxtTitle.Text = video.Title;
            TxtMeta.Text = $"UP: {video.OwnerName}  ·  {FormatDuration(video.Duration)}  ·  共 {video.Pages.Count} P";
            PanelInfo.IsVisible = true;

            _pages.Clear();
            foreach (var p in video.Pages)
                _pages.Add(new PageItem
                {
                    Bvid = p.Bvid,
                    Cid = p.Cid,
                    Page = p.Page,
                    Part = p.Part,
                    Duration = p.Duration,
                    Title = video.Title,
                    IsSelected = true
                });

            _series.Clear();
            if (video.Series.Count > 0)
            {
                TxtSeriesTitle.Text = $"合集「{video.UgcSeasonTitle}」共 {video.Series.Count} 集（可多选）";
                foreach (var s in video.Series)
                    _series.Add(new PageItem
                    {
                        Bvid = s.Bvid,
                        Cid = s.Cid,
                        Page = s.Page,
                        Part = s.Part,
                        Duration = s.Duration,
                        Title = video.UgcSeasonTitle,
                        IsSelected = true
                    });
                PanelSeries.IsVisible = true;
            }
            else
            {
                PanelSeries.IsVisible = false;
            }

            SetStatus($"解析成功：{video.Title}");
            _ = LoadQualitiesAsync(video);
        }
        catch (Exception ex)
        {
            SetStatus($"解析失败：{ex.Message}");
        }
        finally
        {
            BtnParse.IsEnabled = true;
            BtnParse.Content = "解析视频";
        }
    }

    private async Task LoadQualitiesAsync(VideoInfo video)
    {
        try
        {
            var cid = video.Pages.FirstOrDefault()?.Cid ?? 0;
            var data = await BiliClient.GetPlayUrl(video.Bvid, cid, 32, preferSingleFile: true);
            if (data.Qualities.Count == 0) return;

            var supported = new HashSet<int>(data.Qualities.Select(q => q.Quality));
            CmbQuality.Items.Clear();
            AddQuality("1080P", 80);
            AddQuality("720P", 64);
            AddQuality("480P", 32);
            AddQuality("360P", 16);

            for (int i = 0; i < CmbQuality.Items.Count; i++)
                if (CmbQuality.Items[i] is ComboBoxItem ci)
                    ci.IsEnabled = supported.Contains((int)(ci.Tag ?? 0));

            var best = data.Qualities.OrderByDescending(x => x.Quality).FirstOrDefault();
            if (best == null) return;
            for (int i = 0; i < CmbQuality.Items.Count; i++)
                if (CmbQuality.Items[i] is ComboBoxItem ci && (int)(ci.Tag ?? 0) == best.Quality)
                    CmbQuality.SelectedIndex = i;
        }
        catch
        {
            // 画质探测失败不影响默认选项
        }
    }

    private void BtnSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var p in _pages) p.IsSelected = true;
        foreach (var p in _series) p.IsSelected = true;
    }

    private void BtnDeselectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var p in _pages) p.IsSelected = false;
        foreach (var p in _series) p.IsSelected = false;
    }

    // ==================== 加入队列 ====================

    private async void BtnPickDir_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var uri = await FolderPickerActivity.PickAsync();
            if (string.IsNullOrEmpty(uri)) return;

            OutputFolder.Save(uri);
            TxtSaveDir.Text = OutputFolder.DisplayName!;
            SetStatus($"保存目录：{OutputFolder.DisplayName}");
        }
        catch (Exception ex)
        {
            SetStatus($"选择目录失败：{ex.Message}");
        }
    }

    private void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        var selectedPages = _pages.Where(p => p.IsSelected).ToList();
        var selectedSeries = _series.Where(p => p.IsSelected).ToList();
        if (selectedPages.Count == 0 && selectedSeries.Count == 0)
        {
            SetStatus("请先勾选要下载的内容");
            return;
        }

        var dir = Config.Current.OutputDir;
        if (string.IsNullOrEmpty(dir)) { SetStatus("下载目录无效"); return; }
        try { Directory.CreateDirectory(dir); }
        catch { SetStatus($"无法创建下载目录：{dir}"); return; }

        Config.Current.OutputDir = dir;
        Config.Save();

        var audioOnly = ChkAudio.IsChecked == true;
        var withSubtitle = !audioOnly && ChkSubtitle.IsChecked == true;
        var mode = audioOnly
            ? DownloadMode.AudioOnly
            : withSubtitle ? DownloadMode.Package : DownloadMode.Video;

        int count = 0;
        foreach (var p in selectedPages) { _queue.Add(CreateItem(p)); count++; }
        foreach (var p in selectedSeries) { _queue.Add(CreateItem(p)); count++; }

        UpdateQueueUI();
        SetStatus(audioOnly
            ? $"已加入 {count} 个音频任务 → {dir}"
            : $"已加入 {count} 个任务 → {dir}");
        return;

        QueueItem CreateItem(PageItem p) => new(new DownloadTask
        {
            Bvid = p.Bvid,
            Cid = p.Cid,
            Title = p.Title,
            PartTitle = p.Part,
            Mode = mode,
            OutputDir = dir,
            AudioFormat = "m4a",
            PreferSingleFile = true
        });
    }

    // ==================== 下载控制 ====================

    private void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        var tasks = _queue.Where(q => q.Task.Status is "等待中" or "已暂停" or "失败").ToList();
        if (tasks.Count == 0) { SetStatus("没有可开始的任务"); return; }

        var qn = SelectedQuality;
        var max = Math.Max(1, ParallelCount);
        var gate = new SemaphoreSlim(max);

        SetStatus($"开始下载 {tasks.Count} 个任务（并行 {max}）...");
        DownloadForegroundService.Start($"正在下载 {tasks.Count} 个任务");

        foreach (var item in tasks)
        {
            var local = item;
            local.Task.Status = "排队中";
            local.Task.StepText = "等待启动...";
            local.Task.Error = null;
            local.Task.DownloadedBytes = 0;
            local.Task.TotalBytes = 0;
            local.Refresh();

            _ = Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    if (local.Removed || local.Task.Status == "已暂停") return;
                    await DownloadProcessor.ProcessAsync(local.Task, qn, new UiProgress(this, local));
                }
                catch (OperationCanceledException)
                {
                    local.Task.Status = "已暂停";
                    local.Task.StepText = "已暂停";
                    Dispatcher.UIThread.Post(local.Refresh);
                }
                catch (Exception ex)
                {
                    local.Task.Status = "失败";
                    local.Task.StepText = ex.Message;
                    Dispatcher.UIThread.Post(() =>
                    {
                        local.Refresh();
                        SetStatus($"下载失败：{ex.Message}");
                    });
                }
                finally
                {
                    gate.Release();
                }
            });
        }
    }

    private sealed class UiProgress(MobileMainView view, QueueItem item) : IDownloadProgress
    {
        public void Refresh() => Dispatcher.UIThread.Post(() =>
        {
            item.Refresh();
            view.UpdateQueueUI();
        });

        public void Finish(DownloadTask task, string step)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                task.StepText = step;
                task.Status = "完成";
                task.DownloadedBytes = task.TotalBytes;
                item.Refresh();
                view.UpdateQueueUI();
                view.SetStatus($"完成：{task.DisplayTitle}");

                if (!OutputFolder.IsCustom || string.IsNullOrEmpty(task.OutputFile)) return;

                try
                {
                    if (await OutputFolder.PublishAsync(task.OutputFile!))
                        view.SetStatus($"已保存到「{OutputFolder.DisplayName}」：{task.DisplayTitle}");
                    else
                        view.SetStatus("完成，但复制到所选目录失败（可在文件管理中手动移出）");
                }
                catch (Exception ex)
                {
                    view.SetStatus($"完成，但复制到所选目录失败：{ex.Message}");
                }
            });
        }

        public void Fail(DownloadTask task, string msg)
        {
            Dispatcher.UIThread.Post(() =>
            {
                task.StepText = msg;
                task.Status = "失败";
                item.Refresh();
                view.UpdateQueueUI();
                view.SetStatus(msg);
            });
        }

        public void OnPaused()
        {
            Dispatcher.UIThread.Post(() =>
            {
                item.Refresh();
                view.UpdateQueueUI();
            });
        }
    }

    private void BtnPause_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Button b || b.CommandParameter is not string id) return;
        var item = _queue.FirstOrDefault(x => x.Id == id);
        if (item == null) return;

        // 排队中的任务尚未开始（无 Cts），直接把状态改为已暂停，后台线程启动时会跳过
        if (item.Task.Status == "排队中")
        {
            item.Task.Status = "已暂停";
            item.Task.StepText = "已暂停";
            item.Refresh();
            SetStatus("已暂停：排队任务不会再启动");
            return;
        }
        if (item.Task.Status != "下载中") return;

        item.Task.Status = "暂停中";
        item.Task.StepText = "暂停中...";
        item.Task.Cts?.Cancel();
        item.Refresh();
        SetStatus("正在暂停...");
    }

    private void BtnPauseAll_Click(object? sender, RoutedEventArgs e)
    {
        var running = _queue.Where(x => x.Task.Status is "下载中" or "排队中").ToList();
        if (running.Count == 0) { SetStatus("没有正在下载的任务"); return; }

        foreach (var q in running)
        {
            if (q.Task.Status == "排队中")
            {
                q.Task.Status = "已暂停";
                q.Task.StepText = "已暂停";
                q.Refresh();
                continue;
            }
            q.Task.Status = "暂停中";
            q.Task.StepText = "暂停中...";
            q.Task.Cts?.Cancel();
            q.Refresh();
        }
        SetStatus($"已暂停 {running.Count} 个任务");
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Button b || b.CommandParameter is not string id) return;
        var item = _queue.FirstOrDefault(x => x.Id == id);
        if (item == null) return;

        item.Removed = true;
        item.Task.Cts?.Cancel();
        _queue.Remove(item);
        UpdateQueueUI();
        SetStatus($"已移除：{item.DisplayTitle}");
    }

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        var removable = _queue.Where(x => x.Task.Status is not ("排队中" or "下载中" or "暂停中")).ToList();
        if (removable.Count == 0) { SetStatus("所有任务都在下载中，请等待或先暂停"); return; }

        foreach (var r in removable) _queue.Remove(r);
        UpdateQueueUI();
        SetStatus($"已移除 {removable.Count} 个任务" + (_queue.Count > 0 ? "（下载中的任务已保留）" : ""));
    }

    // ==================== 登录 ====================

    private void BtnLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (Config.Current.Login?.IsLoggedIn == true)
        {
            Config.Current.Login = null;
            Config.Save();
            BiliClient.ApplyLoginCookie();
            UpdateLoginUI();
            SetStatus("已登出");
            return;
        }

        LoginOverlay.IsVisible = true;
        TxtCookie.Text = "";
        TxtLoginStatus.Text = "";
    }

    private void BtnLoginClose_Click(object? sender, RoutedEventArgs e)
    {
        LoginOverlay.IsVisible = false;
    }

    private async void BtnWebLogin_Click(object? sender, RoutedEventArgs e)
    {
        TxtLoginStatus.Text = "正在打开 B站 登录页...";
        try
        {
            var cookie = await LoginWebActivity.LoginAsync();
            if (string.IsNullOrWhiteSpace(cookie))
            {
                TxtLoginStatus.Text = "未完成登录，可重试或粘贴 Cookie";
                return;
            }

            var info = BiliClient.ParseCookie(cookie);
            if (info == null)
            {
                TxtLoginStatus.Text = "未能读取登录凭据，请改用粘贴 Cookie";
                return;
            }

            SaveLogin(info);
        }
        catch (Exception ex)
        {
            TxtLoginStatus.Text = $"打开登录页失败：{ex.Message}";
        }
    }

    private void BtnCookieLogin_Click(object? sender, RoutedEventArgs e)
    {
        var raw = TxtCookie.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)) { TxtLoginStatus.Text = "请先粘贴 Cookie 或 SESSDATA"; return; }

        var info = BiliClient.ParseCookie(raw);
        if (info == null)
        {
            TxtLoginStatus.Text = "无法解析 Cookie，请确认包含 SESSDATA";
            return;
        }
        SaveLogin(info);
    }

    private void SaveLogin(LoginInfo info)
    {
        Config.Current.Login = info;
        Config.Save();
        BiliClient.ApplyLoginCookie();

        LoginOverlay.IsVisible = false;
        TxtCookie.Text = "";
        UpdateLoginUI();
        SetStatus($"已登录：{info.UserName ?? "B站账号"}");
    }

    private void UpdateLoginUI()
    {
        if (Config.Current.Login?.IsLoggedIn == true)
        {
            var name = Config.Current.Login.UserName;
            BtnLogin.Content = string.IsNullOrEmpty(name) ? "已登录" : name;
            BtnLogin.Classes.Clear();
        }
        else
        {
            BtnLogin.Content = "登录";
            BtnLogin.Classes.Clear();
            BtnLogin.Classes.Add("primary");
        }
    }

    // ==================== 辅助 ====================

    private void UpdateQueueUI()
    {
        TxtQueueCount.Text = $"{_queue.Count} 个任务";
        TxtEmpty.IsVisible = _queue.Count == 0;

        long done = _queue.Sum(x => x.Task.DownloadedBytes);
        long total = _queue.Sum(x => x.Task.TotalBytes);
        ProgressAll.Value = total > 0 ? Math.Min(100, done * 100.0 / total) : 0;

        // 前台服务跟随下载状态：有任务就保活并更新进度，全部结束则停止
        var active = _queue.Count(x => x.Task.Status is "下载中" or "排队中" or "暂停中");
        if (active == 0)
        {
            DownloadForegroundService.Stop();
        }
        else if ((DateTime.Now - _lastNotify).TotalSeconds >= 2)
        {
            _lastNotify = DateTime.Now;
            var percent = total > 0 ? (int)(done * 100 / total) : 0;
            DownloadForegroundService.Notify($"正在下载 {active} 个任务 · {percent}%");
        }
    }

    private DateTime _lastNotify;

    // ==================== 亚克力背景 ====================

    private List<Bitmap> _bgFrames = [];
    private DispatcherTimer? _bgTimer;
    private DateTime _lastScrollAt = DateTime.MinValue;
    private int _bgIndex;

    private async Task LoadBackgroundAsync()
    {
        if (BackgroundStore.HasUserBackground)
        {
            var user = BackgroundStore.LoadUserBackground();
            if (user != null)
            {
                _bgTimer?.Stop();
                _bgFrames.Clear();
                ImgBackground.Source = user;
                TxtBackground.Text = "自定义图片";
                return;
            }
        }

        TxtBackground.Text = "默认动态壁纸";
        try
        {
            _bgFrames = await BackgroundStore.LoadFramesAsync();
        }
        catch
        {
            _bgFrames = [];
        }

        if (_bgFrames.Count == 0) return;

        _bgIndex = 0;
        ImgBackground.Source = _bgFrames[0];

        // 83ms/帧 = 12fps，144 帧约 12 秒一轮，速度与原视频一致
        _bgTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(83) };
        _bgTimer.Tick -= BgTimer_Tick;
        _bgTimer.Tick += BgTimer_Tick;
        _bgTimer.Start();
    }

    private void OnMainScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 只记录时间戳，避免在滑动过程中反复创建/销毁 DispatcherTimer 带来额外开销
        _lastScrollAt = DateTime.UtcNow;
    }

    private void BgTimer_Tick(object? sender, EventArgs e)
    {
        // 手指拖动期间跳过背景换帧，让渲染资源优先给滚动；停手 200ms 后自动恢复
        if ((DateTime.UtcNow - _lastScrollAt).TotalMilliseconds < 200) return;
        if (_bgFrames.Count == 0) return;
        _bgIndex = (_bgIndex + 1) % _bgFrames.Count;
        ImgBackground.Source = _bgFrames[_bgIndex];
    }

    private async void BtnPickBackground_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var uri = await ImagePickerActivity.PickAsync();
            if (string.IsNullOrEmpty(uri)) return;

            if (!await BackgroundStore.SaveUserBackgroundAsync(uri))
            {
                SetStatus("背景图保存失败，请换一张图片重试");
                return;
            }

            _bgTimer?.Stop();
            _bgFrames.Clear();
            ImgBackground.Source = BackgroundStore.LoadUserBackground();
            TxtBackground.Text = "自定义图片";
            SetStatus("背景已更新");
        }
        catch (Exception ex)
        {
            SetStatus($"设置背景失败：{ex.Message}");
        }
    }

    private void BtnResetBackground_Click(object? sender, RoutedEventArgs e)
    {
        BackgroundStore.Reset();
        _ = LoadBackgroundAsync();
        SetStatus("已恢复默认动态壁纸");
    }

    private void SetStatus(string message) => TxtStatus.Text = message;

    private static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.Hours > 0
            ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }
}

// ==================== 列表项模型 ====================

public class PageItem : INotifyPropertyChanged
{
    public string Bvid { get; set; } = "";
    public long Cid { get; set; }
    public int Page { get; set; }
    public string Part { get; set; } = "";
    public int Duration { get; set; }
    public string Title { get; set; } = "";

    private bool _selected = true;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnChanged();
        }
    }

    public string DisplayText
    {
        get
        {
            var t = TimeSpan.FromSeconds(Duration);
            var d = t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
            return string.IsNullOrEmpty(Part) ? $"{Title} ({d})" : $"P{Page} {Part} ({d})";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class QueueItem : INotifyPropertyChanged
{
    public DownloadTask Task { get; }
    public QueueItem(DownloadTask task) => Task = task;

    /// <summary>已从队列移除，排队中的任务不再执行</summary>
    public bool Removed { get; set; }

    public string Id => Task.Id;
    public string DisplayTitle => Task.DisplayTitle;
    public string StepText => Task.StepText;
    public double ProgressPercent => Task.TotalBytes > 0
        ? Math.Min(100, (double)Task.DownloadedBytes / Task.TotalBytes * 100)
        : 0;
    public string ProgressText => $"{ProgressPercent:F0}%";
    public string InfoText =>
        $"{Task.Status}  {Downloader.FormatMB(Task.DownloadedBytes)} / {Downloader.FormatMB(Task.TotalBytes)}";

    public void Refresh()
    {
        OnChanged(nameof(StepText));
        OnChanged(nameof(ProgressPercent));
        OnChanged(nameof(ProgressText));
        OnChanged(nameof(InfoText));
        OnChanged(nameof(DisplayTitle));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
