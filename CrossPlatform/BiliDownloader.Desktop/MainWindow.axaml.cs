using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BiliDownloader.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PageItem> _pages = [];
    private readonly ObservableCollection<PageItem> _series = [];
    private readonly ObservableCollection<QueueItem> _queue = [];
    private VideoInfo? _currentVideo;
    private bool _ffMissingWarned;
    private readonly List<Bitmap> _bgFrames = [];
    private DispatcherTimer? _bgTimer;
    private int _bgIndex;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        InitBackground();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>加载内置的预模糊背景帧，按 12fps 循环播放，与手机端视觉保持一致。</summary>
    private void InitBackground()
    {
        try
        {
            for (int i = 0; i < 48; i++)
            {
                using var stream = AssetLoader.Open(
                    new Uri($"avares://BiliDownloader.Desktop/Assets/bg-desktop/frame_{i:000}.jpg"));
                _bgFrames.Add(new Bitmap(stream));
            }
        }
        catch
        {
            _bgFrames.Clear();
            return;
        }

        if (_bgFrames.Count == 0) return;
        ImgBackground.Source = _bgFrames[0];
        // 250ms/帧 = 抽帧时的原始节奏，与 WPF/手机端一致
        _bgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _bgTimer.Tick += (_, _) =>
        {
            _bgIndex = (_bgIndex + 1) % _bgFrames.Count;
            ImgBackground.Source = _bgFrames[_bgIndex];
        };
        _bgTimer.Start();
        Closed += (_, _) => _bgTimer?.Stop();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Config.Load();
        TxtOutDir.Text = Config.Current.OutputDir;
        FillDefaultQualities();
        FillOptions();
        ListPages.ItemsSource = _pages;
        ListSeries.ItemsSource = _series;
        ListQueue.ItemsSource = _queue;
        UpdateLoginUI();
        UpdateFfmpegButton();
    }

    private async void BtnBrowseDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择下载目录" });
        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            TxtOutDir.Text = path;
            Config.Current.OutputDir = path;
            Config.Save();
        }
    }

    private void FillDefaultQualities()
    {
        CmbQuality.Items.Clear();
        AddQ("4K", 120); AddQ("1080P", 80); AddQ("720P", 64);
        AddQ("480P", 32); AddQ("360P", 16);
        CmbQuality.SelectedIndex = 3;
    }
    private void AddQ(string name, int q) => CmbQuality.Items.Add(new ComboBoxItem { Content = name, Tag = q });

    private void FillOptions()
    {
        var retries = new[] { 0, 1, 2, 3, 5 };
        CmbRetry.Items.Clear();
        foreach (var n in retries) CmbRetry.Items.Add(new ComboBoxItem { Content = $"{n}次", Tag = n });
        var ri = Array.IndexOf(retries, Config.Current.RetryCount);
        CmbRetry.SelectedIndex = ri >= 0 ? ri : 2;
        CmbRetry.SelectionChanged += (_, _) => { Config.Current.RetryCount = RetryCount; Config.Save(); };

        var parallels = new[] { 1, 2, 3, 4, 6 };
        CmbParallel.Items.Clear();
        foreach (var n in parallels) CmbParallel.Items.Add(new ComboBoxItem { Content = $"{n}个", Tag = n });
        var pi = Array.IndexOf(parallels, Config.Current.MaxParallel);
        CmbParallel.SelectedIndex = pi >= 0 ? pi : 2;
        CmbParallel.SelectionChanged += (_, _) => { Config.Current.MaxParallel = ParallelCount; Config.Save(); };

        var formats = new[] { ("MP3", "mp3"), ("M4A", "m4a"), ("AAC", "aac"), ("WAV", "wav"), ("FLAC", "flac"), ("OPUS", "opus") };
        CmbAudioFormat.Items.Clear();
        foreach (var (name, tag) in formats) CmbAudioFormat.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
        var fi = Array.FindIndex(formats, f => f.Item2 == Config.Current.AudioFormat);
        CmbAudioFormat.SelectedIndex = fi >= 0 ? fi : 0;
        CmbAudioFormat.SelectionChanged += (_, _) => { Config.Current.AudioFormat = AudioFormat; Config.Save(); };
    }

    private int RetryCount => CmbRetry.SelectedItem is ComboBoxItem c ? (int)(c.Tag ?? 3) : 3;
    private int ParallelCount => CmbParallel.SelectedItem is ComboBoxItem c ? (int)(c.Tag ?? 3) : 3;
    private string AudioFormat => CmbAudioFormat.SelectedItem is ComboBoxItem c ? (string)(c.Tag ?? "mp3") : "mp3";

    private void ChkAudio_Changed(object? sender, RoutedEventArgs e)
        => CmbAudioFormat.IsEnabled = ChkAudio.IsChecked == true;

    private int SelectedQuality =>
        CmbQuality.SelectedItem is ComboBoxItem c ? (int)(c.Tag ?? 32) : 32;

    private void UpdateLoginUI()
    {
        if (Config.Current.Login?.IsLoggedIn == true)
        {
            TxtLogin.Text = $"👤 {Config.Current.Login.UserName ?? "已登录"}";
            BtnLogin.Content = "登出";
            BtnLogin.Classes.Clear();
        }
        else
        {
            TxtLogin.Text = "未登录";
            BtnLogin.Content = "🔑 登录";
            BtnLogin.Classes.Clear();
            BtnLogin.Classes.Add("primary");
        }
    }

    // ==================== 解析 ====================

    private void TxtUrl_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = ParseAsync();
    }

    private async void BtnParse_Click(object? sender, RoutedEventArgs e) => await ParseAsync();

    private async Task ParseAsync()
    {
        var url = TxtUrl.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) { SetStatus("请输入链接"); return; }
        var (type, id) = await BiliClient.ParseUrlAsync(url);
        if (type != "video") { SetStatus("无法识别链接"); return; }

        BtnParse.IsEnabled = false;
        BtnParse.Content = "解析中...";
        SetStatus("正在解析...");
        try
        {
            var video = await BiliClient.GetVideoInfo(id);
            _currentVideo = video;
            TxtTitle.Text = video.Title;
            TxtMeta.Text = $"UP: {video.OwnerName}  ·  {FormatDur(video.Duration)}  ·  共{video.Pages.Count}P";
            PanelInfo.IsVisible = true;

            _pages.Clear();
            foreach (var p in video.Pages)
                _pages.Add(new PageItem { Bvid = p.Bvid, Cid = p.Cid, Page = p.Page, Part = p.Part, Duration = p.Duration, Title = video.Title, IsSelected = true });

            _series.Clear();
            if (video.Series.Count > 0)
            {
                TxtSeriesTitle.Text = $"📚 合集「{video.UgcSeasonTitle}」 共{video.Series.Count}集（可多选批量下载）";
                foreach (var s in video.Series)
                    _series.Add(new PageItem { Bvid = s.Bvid, Cid = s.Cid, Page = s.Page, Part = s.Part, Duration = s.Duration, Title = video.UgcSeasonTitle, IsSelected = true });
                PanelSeries.IsVisible = true;
            }
            else PanelSeries.IsVisible = false;

            SetStatus($"解析成功：{video.Title}");
            _ = LoadQualitiesAsync(video);
        }
        catch (Exception ex) { SetStatus($"❌ 解析失败: {ex.Message}"); }
        finally { BtnParse.IsEnabled = true; BtnParse.Content = "🔍 解析"; }
    }

    private async Task LoadQualitiesAsync(VideoInfo video)
    {
        try
        {
            var cid = video.Pages.FirstOrDefault()?.Cid ?? 0;
            var data = await BiliClient.GetPlayUrl(video.Bvid, cid, 32);
            if (data.Qualities.Count > 0)
            {
                var supported = new HashSet<int>(data.Qualities.Select(q => q.Quality));
                CmbQuality.Items.Clear();
                AddQ("4K", 120); AddQ("1080P", 80); AddQ("720P", 64); AddQ("480P", 32); AddQ("360P", 16);
                for (int i = 0; i < CmbQuality.Items.Count; i++)
                    if (CmbQuality.Items[i] is ComboBoxItem ci)
                        ci.IsEnabled = supported.Contains((int)(ci.Tag ?? 0));
                var best = data.Qualities.OrderByDescending(x => x.Quality).FirstOrDefault();
                if (best != null)
                {
                    for (int i = 0; i < CmbQuality.Items.Count; i++)
                        if (CmbQuality.Items[i] is ComboBoxItem ci && (int)(ci.Tag ?? 0) == best.Quality)
                            CmbQuality.SelectedIndex = i;
                }
            }
        }
        catch { }
    }

    private void BtnSelectAll_Click(object? s, RoutedEventArgs e)
    {
        foreach (var p in _pages) p.IsSelected = true;
        foreach (var p in _series) p.IsSelected = true;
    }
    private void BtnDeselectAll_Click(object? s, RoutedEventArgs e)
    {
        foreach (var p in _pages) p.IsSelected = false;
        foreach (var p in _series) p.IsSelected = false;
    }

    // ==================== Shift 批量多选 ====================
    private PageItem? _pageAnchor;
    private PageItem? _seriesAnchor;

    private void PageCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not PageItem item) return;
        _pageAnchor = item;
    }

    private void SeriesCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not PageItem item) return;
        _seriesAnchor = item;
    }

    private static void SelectRange(ObservableCollection<PageItem> list, PageItem from, PageItem to)
    {
        var i1 = list.IndexOf(from);
        var i2 = list.IndexOf(to);
        if (i1 < 0 || i2 < 0) return;
        var min = Math.Min(i1, i2);
        var max = Math.Max(i1, i2);
        bool select = to.IsSelected;
        for (int i = min; i <= max; i++) list[i].IsSelected = select;
    }

    // ==================== 加入队列 ====================

    private void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        var selectedPages = _pages.Where(p => p.IsSelected).ToList();
        var selectedSeries = _series.Where(p => p.IsSelected).ToList();
        if (selectedPages.Count == 0 && selectedSeries.Count == 0) { SetStatus("请先勾选要下载的内容"); return; }
        var dir = TxtOutDir.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(dir)) dir = Config.Current.OutputDir;
        if (string.IsNullOrEmpty(dir)) { SetStatus("请先选择下载目录"); return; }
        var mode = ChkAudio.IsChecked == true ? DownloadMode.AudioOnly : DownloadMode.Video;
        int n = 0;
        foreach (var p in selectedPages)
        {
            _queue.Add(new QueueItem(new DownloadTask
            {
                Bvid = p.Bvid, Cid = p.Cid, Title = p.Title, PartTitle = p.Part,
                Mode = mode, OutputDir = dir, AudioFormat = mode == DownloadMode.AudioOnly ? AudioFormat : "mp3"
            }));
            n++;
        }
        foreach (var s in selectedSeries)
        {
            _queue.Add(new QueueItem(new DownloadTask
            {
                Bvid = s.Bvid, Cid = s.Cid, Title = s.Title, PartTitle = s.Part,
                Mode = mode, OutputDir = dir, AudioFormat = mode == DownloadMode.AudioOnly ? AudioFormat : "mp3"
            }));
            n++;
        }
        TxtQueueCount.Text = $"{_queue.Count} 个任务";
        TxtEmpty.IsVisible = false;
        SetStatus(mode == DownloadMode.AudioOnly ? $"已加入 {n} 个音频任务 → {dir}" : $"已加入 {n} 个任务 → {dir}");
    }

    // ==================== 下载 ====================

    private async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        var tasks = _queue.Where(q => q.Task.Status is "等待中" or "已暂停").ToList();
        if (tasks.Count == 0) { SetStatus("没有可开始的任务"); return; }

        if (Merger.FindFfmpeg() == null && !_ffMissingWarned)
        {
            _ffMissingWarned = true;
            var r = await MessageBox.Show(this,
                "当前未安装 ffmpeg。\n\n视频将以兼容模式拼接，音频将以M4A格式保存。\n\n是否现在下载 ffmpeg？",
                "未安装 ffmpeg", MessageBox.MessageBoxButtons.YesNo);
            if (r == MessageBox.MessageBoxResult.Yes)
            {
                SetStatus("正在下载 ffmpeg...");
                var (ff, _) = await Merger.DownloadFfmpeg();
                if (ff != null) { UpdateFfmpegButton(); SetStatus("ffmpeg 安装成功"); }
                else SetStatus("ffmpeg 下载失败，将使用兼容模式");
            }
        }

        var qn = SelectedQuality;
        var max = Math.Max(1, ParallelCount);
        var gate = new SemaphoreSlim(max);
        SetStatus($"开始下载 {tasks.Count} 个任务（并行 {max}）...");
        foreach (var q in tasks)
        {
            var local = q;
            _ = Task.Run(async () =>
            {
                await gate.WaitAsync();
                try { await DownloadProcessor.ProcessAsync(local.Task, qn, new UiProgress(this, local)); }
                finally { gate.Release(); }
            });
        }
    }

    private class UiProgress(MainWindow win, QueueItem q) : IDownloadProgress
    {
        public void Refresh() => q.Refresh();
        public void Finish(DownloadTask task, string step)
        {
            q.Task.StepText = step;
            q.Task.Status = "完成";
            q.Task.DownloadedBytes = q.Task.TotalBytes;
            q.Refresh();
            Dispatcher.UIThread.Post(() => { win.SetStatus($"完成: {q.Task.DisplayTitle}"); win.UpdateAll(); });
        }
        public void Fail(DownloadTask task, string msg)
        {
            q.Task.StepText = msg;
            q.Task.Status = "失败";
            q.Refresh();
            Dispatcher.UIThread.Post(() => { win.SetStatus(msg); win.UpdateAll(); });
        }
        public void OnPaused() => Dispatcher.UIThread.Post(() => win.UpdateAll());
    }

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        var removable = _queue.Where(x => x.Task.Status != "下载中").ToList();
        if (removable.Count == 0) { SetStatus("所有任务都在下载中，请等待完成后再清空"); return; }
        foreach (var r in removable) _queue.Remove(r);
        TxtQueueCount.Text = $"{_queue.Count} 个任务";
        TxtEmpty.IsVisible = _queue.Count == 0;
        SetStatus($"已移除 {removable.Count} 个任务" + (_queue.Count > 0 ? "（下载中的任务已保留）" : ""));
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.CommandParameter is string id)
        {
            var item = _queue.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                _queue.Remove(item);
                TxtQueueCount.Text = $"{_queue.Count} 个任务";
                TxtEmpty.IsVisible = _queue.Count == 0;
            }
        }
    }

    // ==================== 暂停 ====================

    private void BtnPause_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.CommandParameter is string id)
        {
            var item = _queue.FirstOrDefault(x => x.Id == id);
            if (item != null && item.Task.Status == "下载中")
            {
                item.Task.Status = "暂停中";
                item.Task.StepText = "暂停中...";
                item.Task.Cts?.Cancel();
                item.Refresh();
            }
        }
    }

    private void BtnPauseAll_Click(object? sender, RoutedEventArgs e)
    {
        var running = _queue.Where(x => x.Task.Status == "下载中").ToList();
        if (running.Count == 0) { SetStatus("没有正在下载的任务"); return; }
        foreach (var q in running)
        {
            q.Task.Status = "暂停中";
            q.Task.StepText = "暂停中...";
            q.Task.Cts?.Cancel();
            q.Refresh();
        }
        SetStatus($"已暂停 {running.Count} 个任务");
    }

    // ==================== 缓存目录 ====================

    private async void BtnBrowseCache_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择缓存文件夹" });
        if (folders.Count > 0)
        {
            Config.Current.CacheDir = folders[0].Path.LocalPath;
            Config.Save();
            SetStatus($"缓存目录: {Config.Current.CacheDir}");
        }
    }

    // ==================== ffmpeg ====================

    private async void BtnFfmpeg_Click(object? sender, RoutedEventArgs e)
    {
        var existing = Merger.FindFfmpeg();
        if (existing != null)
        {
            await MessageBox.Show(this, $"ffmpeg 已安装：\n{existing}", "ffmpeg", MessageBox.MessageBoxButtons.OK);
            return;
        }
        var r = await MessageBox.Show(this,
            "尚未安装 ffmpeg。\n\n视频无损合并、音频转码需要它。\n\n是否立即下载 ffmpeg？",
            "安装 ffmpeg", MessageBox.MessageBoxButtons.YesNo);
        if (r != MessageBox.MessageBoxResult.Yes) return;
        SetStatus("正在下载 ffmpeg...");
        var (ff, err) = await Merger.DownloadFfmpeg();
        if (ff != null)
        {
            UpdateFfmpegButton();
            SetStatus("ffmpeg 安装成功");
            await MessageBox.Show(this, $"ffmpeg 安装成功！\n{ff}", "安装成功", MessageBox.MessageBoxButtons.OK);
        }
        else
        {
            SetStatus($"ffmpeg 安装失败: {err}");
            await MessageBox.Show(this, $"ffmpeg 自动下载失败：{err}", "安装失败", MessageBox.MessageBoxButtons.OK);
        }
    }

    private void UpdateFfmpegButton()
    {
        BtnFfmpeg.Content = Merger.FindFfmpeg() != null ? "✓ ffmpeg" : "⬇ ffmpeg";
    }

    // ==================== 登录 ====================

    private async void BtnLogin_Click(object? sender, RoutedEventArgs e)
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
        var r = await MessageBox.Show(this, "选择登录方式：\n\n是 - 扫码登录\n否 - 粘贴Cookie", "登录", MessageBox.MessageBoxButtons.YesNoCancel);
        if (r == MessageBox.MessageBoxResult.Yes) _ = ShowQrAsync();
        else if (r == MessageBox.MessageBoxResult.No) await PasteCookie();
    }

    private async Task ShowQrAsync()
    {
        var win = new Window { Title = "扫码登录", Width = 320, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = "请用B站App扫码", FontSize = 14, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 12) });
        var status = new TextBlock { Text = "正在生成二维码...", FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(status);
        win.Content = sp;
        win.Show();

        try
        {
            var (key, url) = await BiliClient.GenQr();
            var qr = new QRCoder.QRCodeGenerator();
            var data = qr.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
            var matrix = data.ModuleMatrix;
            var n = matrix.Count;
            var bmp = new WriteableBitmap(new PixelSize(260, 260), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
            {
                unsafe
                {
                    var ptr = (byte*)fb.Address;
                    for (int y = 0; y < 260; y++)
                        for (int x = 0; x < 260; x++)
                        {
                            int my = Math.Min((int)((double)y * n / 260), n - 1);
                            int mx = Math.Min((int)((double)x * n / 260), n - 1);
                            bool dark = matrix[my][mx];
                            byte val = dark ? (byte)0 : (byte)255;
                            var off = (y * 260 + x) * 4;
                            ptr[off] = val;
                            ptr[off + 1] = val;
                            ptr[off + 2] = val;
                            ptr[off + 3] = 255;
                        }
                }
            }
            sp.Children.Insert(1, new Image { Source = bmp, Width = 260, Height = 260 });
            status.Text = "等待扫码...";

            var cts = new CancellationTokenSource();
            win.Closed += (_, _) => cts.Cancel();
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var (code, redirect) = await BiliClient.PollQr(key);
                    await Dispatcher.UIThread.InvokeAsync(() => status.Text = code switch
                    {
                        86101 => "等待扫码...", 86090 => "已扫码，请在手机上确认", 0 => "登录成功！", _ => "已过期"
                    });
                    if (code == 0 && redirect != null)
                    {
                        var info = await BiliClient.GetLogin(redirect);
                        if (info != null)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                Config.Current.Login = info; Config.Save(); BiliClient.ApplyLoginCookie(); UpdateLoginUI(); win.Close();
                            });
                        }
                        else
                        {
                            await Dispatcher.UIThread.InvokeAsync(() => status.Text = "提取Cookie失败，请改用粘贴Cookie");
                        }
                        break;
                    }
                    if (code == 86038) break;
                    try { await Task.Delay(2000, cts.Token); } catch { }
                }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => status.Text = $"二维码加载失败: {ex.Message}\n请改用粘贴Cookie");
            SetStatus($"登录失败: {ex.Message}");
        }
    }

    private async Task PasteCookie()
    {
        var win = new Window { Title = "粘贴Cookie", Width = 400, Height = 180, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var sp = new StackPanel { Margin = new Thickness(16) };
        var tb = new TextBox { Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(new TextBlock { Text = "粘贴 SESSDATA 或完整Cookie:", Margin = new Thickness(0, 0, 0, 8) });
        sp.Children.Add(tb);
        var ok = new Button { Content = "确认", Width = 70, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var confirmed = false;
        ok.Click += (_, _) => { confirmed = true; win.Close(); };
        sp.Children.Add(ok);
        win.Content = sp;
        await win.ShowDialog(this);
        if (confirmed)
        {
            var info = BiliClient.ParseCookie(tb.Text?.Trim() ?? "");
            if (info != null)
            {
                Config.Current.Login = info; Config.Save(); BiliClient.ApplyLoginCookie(); UpdateLoginUI();
                SetStatus("Cookie已保存");
            }
            else SetStatus("无法解析Cookie");
        }
    }

    // ==================== 辅助 ====================

    private void UpdateAll()
    {
        TxtQueueCount.Text = $"{_queue.Count} 个任务";
        int done = _queue.Count(x => x.Task.Status == "完成");
        int total = _queue.Count(x => x.Task.Status != "等待中");
        long bytes = _queue.Sum(x => x.Task.DownloadedBytes);
        long all = _queue.Sum(x => x.Task.TotalBytes);
        ProgressAll.Value = all > 0 ? bytes * 100.0 / all : 0;
    }

    private static string FormatDur(int s)
    {
        var t = TimeSpan.FromSeconds(s);
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private void SetStatus(string msg) => TxtStatus.Text = msg;
}

// ==================== ViewModel ====================

public class PageItem : INotifyPropertyChanged
{
    public string Bvid { get; set; } = "";
    public long Cid { get; set; }
    public int Page { get; set; }
    public string Part { get; set; } = "";
    public int Duration { get; set; }
    public string Title { get; set; } = "";
    private bool _sel = true;
    public bool IsSelected { get => _sel; set { _sel = value; OnChanged(); } }
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
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class QueueItem : INotifyPropertyChanged
{
    public DownloadTask Task { get; }
    public QueueItem(DownloadTask t) { Task = t; }
    public string Id => Task.Id;
    public string DisplayTitle => Task.DisplayTitle;
    public string StepText => Task.StepText;
    public double ProgressPercent => Task.TotalBytes > 0 ? (double)Task.DownloadedBytes / Task.TotalBytes * 100 : 0;
    public string ProgressText => $"{ProgressPercent:F0}%";
    public string InfoText => $"{Task.Status}  {Downloader.FormatMB(Task.DownloadedBytes)} / {Downloader.FormatMB(Task.TotalBytes)}";
    public void Refresh() { OnChanged(nameof(StepText)); OnChanged(nameof(ProgressPercent)); OnChanged(nameof(ProgressText)); OnChanged(nameof(InfoText)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
