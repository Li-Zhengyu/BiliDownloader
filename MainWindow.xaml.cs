namespace BiliDownloader;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PageItem> _pages = [];
    private readonly ObservableCollection<PageItem> _series = [];
    private readonly ObservableCollection<QueueItem> _queue = [];
    private readonly Downloader _dl = new();
    private VideoInfo? _currentVideo;
    private bool _ffMissingWarned;   // 下载前"未安装ffmpeg"提醒只弹一次
    private readonly List<BitmapImage> _bgFrames = [];
    private DispatcherTimer? _bgTimer;
    private int _bgIndex;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        InitBackground();
    }

    /// <summary>背景：优先直接播放原视频（原始帧率），失败时回退到内置帧序列。</summary>
    private void InitBackground()
    {
        LoadFrameFallback();

        BgVideo.MediaOpened += (_, _) => _bgTimer?.Stop();
        BgVideo.MediaEnded += (_, _) =>
        {
            BgVideo.Position = TimeSpan.Zero;
            BgVideo.Play();
        };
        BgVideo.MediaFailed += (_, _) => _bgTimer?.Start();

        // single-file 发布下 MediaElement 无法直接读 pack:// 流，先释放到临时文件再播放
        try
        {
            var res = Application.GetResourceStream(
                new Uri("pack://application:,,,/bg-desktop/bg_loop.mp4", UriKind.Absolute));
            if (res != null)
            {
                var videoPath = Path.Combine(Path.GetTempPath(), "BiliDownloader", "bg_loop.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
                using (var fs = File.Create(videoPath)) res.Stream.CopyTo(fs);
                BgVideo.Source = new Uri(videoPath);
                BgVideo.Play();
            }
        }
        catch
        {
            // 释放失败则继续使用帧序列背景
        }

        Closed += (_, _) => _bgTimer?.Stop();
    }

    /// <summary>帧序列兜底：加载内置的预模糊背景帧，按 12fps 循环播放。</summary>
    private void LoadFrameFallback()
    {
        try
        {
            for (int i = 0; i < 48; i++)
            {
                var frame = new BitmapImage();
                frame.BeginInit();
                frame.UriSource = new Uri($"pack://application:,,,/bg-desktop/frame_{i:000}.jpg", UriKind.Absolute);
                frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.EndInit();
                frame.Freeze();
                _bgFrames.Add(frame);
            }
        }
        catch
        {
            _bgFrames.Clear();
            return;
        }

        if (_bgFrames.Count == 0) return;
        ImgBackground.Source = _bgFrames[0];
        _bgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _bgTimer.Tick += (_, _) =>
        {
            _bgIndex = (_bgIndex + 1) % _bgFrames.Count;
            ImgBackground.Source = _bgFrames[_bgIndex];
        };
        _bgTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
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

    private void BtnBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择下载目录" };
        if (dlg.ShowDialog() == true)
        {
            TxtOutDir.Text = dlg.FolderName;
            Config.Current.OutputDir = dlg.FolderName;
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

    private void ChkAudio_Changed(object sender, RoutedEventArgs e)
        => CmbAudioFormat.IsEnabled = ChkAudio.IsChecked == true;

    private int SelectedQuality =>
        CmbQuality.SelectedItem is ComboBoxItem c ? (int)(c.Tag ?? 32) : 32;

    private void UpdateLoginUI()
    {
        if (Config.Current.Login?.IsLoggedIn == true)
        {
            TxtLogin.Text = $"👤 {Config.Current.Login.UserName ?? "已登录"}";
            BtnLogin.Content = "登出";
            BtnLogin.Style = (Style)FindResource(typeof(Button));   // 登出用普通小按钮
        }
        else
        {
            TxtLogin.Text = "未登录";
            BtnLogin.Content = "🔑 登录";
            BtnLogin.Style = (Style)FindResource("PrimaryButton"); // 登录用蓝色小按钮
        }
    }

    // ==================== 解析 ====================

    private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = ParseAsync();
    }

    private async void BtnParse_Click(object sender, RoutedEventArgs e) => await ParseAsync();

    private async Task ParseAsync()
    {
        var url = TxtUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) { SetStatus("请输入链接"); return; }
        var (type, id) = BiliClient.ParseUrl(url);
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
            PanelInfo.Visibility = Visibility.Visible;

            _pages.Clear();
            foreach (var p in video.Pages)
                _pages.Add(new PageItem { Bvid = p.Bvid, Cid = p.Cid, Page = p.Page, Part = p.Part, Duration = p.Duration, Title = video.Title, IsSelected = true });

            // UGC 合集（多集）
            _series.Clear();
            if (video.Series.Count > 0)
            {
                TxtSeriesTitle.Text = $"📚 合集「{video.UgcSeasonTitle}」 共{video.Series.Count}集（可多选批量下载）";
                foreach (var s in video.Series)
                    _series.Add(new PageItem { Bvid = s.Bvid, Cid = s.Cid, Page = s.Page, Part = s.Part, Duration = s.Duration, Title = video.UgcSeasonTitle, IsSelected = true });
                PanelSeries.Visibility = Visibility.Visible;
            }
            else PanelSeries.Visibility = Visibility.Collapsed;

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
                // 合并：保留完整默认列表（360P~4K），禁用 API 不支持的画质项
                var supported = new HashSet<int>(data.Qualities.Select(q => q.Quality));
                CmbQuality.Items.Clear();
                AddQ("4K", 120); AddQ("1080P", 80); AddQ("720P", 64); AddQ("480P", 32); AddQ("360P", 16);
                for (int i = 0; i < CmbQuality.Items.Count; i++)
                    if (CmbQuality.Items[i] is ComboBoxItem ci)
                        ci.IsEnabled = supported.Contains((int)(ci.Tag ?? 0));
                // 默认选最高可用
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

    private void BtnSelectAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var p in _pages) p.IsSelected = true;
        foreach (var p in _series) p.IsSelected = true;
    }
    private void BtnDeselectAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var p in _pages) p.IsSelected = false;
        foreach (var p in _series) p.IsSelected = false;
    }

    // ==================== Shift 批量多选 ====================
    private PageItem? _pageAnchor;
    private PageItem? _seriesAnchor;

    private void PageCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not PageItem item) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _pageAnchor != null)
            SelectRange(_pages, _pageAnchor, item);
        _pageAnchor = item;
    }

    private void SeriesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not PageItem item) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _seriesAnchor != null)
            SelectRange(_series, _seriesAnchor, item);
        _seriesAnchor = item;
    }

    private static void SelectRange(ObservableCollection<PageItem> list, PageItem from, PageItem to)
    {
        var i1 = list.IndexOf(from);
        var i2 = list.IndexOf(to);
        if (i1 < 0 || i2 < 0) return;
        var min = Math.Min(i1, i2);
        var max = Math.Max(i1, i2);
        // 以点击项当前状态为准：点的是未选中项 → 范围全选；点的是已选中项 → 范围全取消
        bool select = to.IsSelected;
        for (int i = min; i <= max; i++) list[i].IsSelected = select;
    }

    // ==================== 加入队列 ====================

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var selectedPages = _pages.Where(p => p.IsSelected).ToList();
        var selectedSeries = _series.Where(p => p.IsSelected).ToList();
        if (selectedPages.Count == 0 && selectedSeries.Count == 0) { SetStatus("请先勾选要下载的内容"); return; }
        var dir = TxtOutDir.Text.Trim();
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
        TxtEmpty.Visibility = Visibility.Collapsed;
        SetStatus(mode == DownloadMode.AudioOnly
            ? $"已加入 {n} 个音频任务 → {dir}"
            : $"已加入 {n} 个任务 → {dir}");
    }

    // ==================== 下载 ====================

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var tasks = _queue.Where(q => q.Task.Status is "等待中" or "已暂停").ToList();
        if (tasks.Count == 0) { SetStatus("没有可开始的任务"); return; }

        // 未安装ffmpeg时提醒一次（仅提醒，不阻塞下载）
        if (Merger.FindFfmpeg() == null && !_ffMissingWarned)
        {
            _ffMissingWarned = true;
            var r = MessageBox.Show(
                "当前未安装 ffmpeg。\n\n" +
                "视频将以兼容模式拼接（无需ffmpeg），音频将以M4A格式保存。\n\n" +
                "是否现在下载 ffmpeg？（推荐：支持无损合并与高音质音频转码）",
                "未安装 ffmpeg", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (r == MessageBoxResult.Yes)
            {
                SetStatus("正在下载 ffmpeg...");
                var (ff, _) = await Merger.DownloadFfmpeg();
                if (ff != null) { UpdateFfmpegButton(); SetStatus("ffmpeg 安装成功"); }
                else SetStatus("ffmpeg 下载失败，将使用兼容模式");
            }
        }

        var qn = SelectedQuality;   // ★ UI线程读取画质，后台线程不能碰CmbQuality
        var max = Math.Max(1, ParallelCount);   // 并行任务数上限
        var gate = new SemaphoreSlim(max);
        SetStatus($"开始下载 {tasks.Count} 个任务（并行 {max}）...");
        foreach (var q in tasks)
        {
            var local = q;
            _ = Task.Run(async () =>
            {
                await gate.WaitAsync();
                try { await ProcessAsync(local, qn); }
                finally { gate.Release(); }
            });
        }
    }

    private async Task ProcessAsync(QueueItem q, int qn)
    {
        var task = q.Task;
        string? tmpDir = null;
        try
        {
            task.Cts = new CancellationTokenSource();
            task.Status = "下载中";
            q.Refresh();

            var play = await BiliClient.GetPlayUrl(task.Bvid, task.Cid, qn, task.Mode == DownloadMode.AudioOnly);
            task.TotalBytes = play.DurlSize > 0 ? play.DurlSize : 1;
            q.Refresh();

            // 字幕模式
            if (task.Mode is DownloadMode.SubtitleOnly or DownloadMode.Package)
            {
                var subs = await BiliClient.GetSubtitles(task.Bvid, task.Cid);
                var sub = subs.FirstOrDefault(s => s.Lan.Contains("zh")) ?? subs.FirstOrDefault();
                if (sub == null)
                {
                    if (task.Mode == DownloadMode.SubtitleOnly) { Fail(q, "❌ 无可用字幕"); return; }
                    // Package 模式：无字幕则跳过字幕，继续下载视频
                }
                else
                {
                    task.StepText = "下载字幕";
                    q.Refresh();
                    var json = await BiliClient.DownloadSubtitle(sub.SubtitleUrl);
                    var srt = SubtitleConverter.JsonToSrt(json);
                    var subPath = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.srt");
                    await File.WriteAllTextAsync(subPath, srt);
                    if (task.Mode == DownloadMode.SubtitleOnly)
                    {
                        task.OutputFile = subPath;
                        Finish(q, "✅ 完成");
                        return;
                    }
                }
            }

            // durl 单文件（最可靠）
            if (!string.IsNullOrEmpty(play.DurlUrl))
            {
                task.StepText = "下载视频...";
                q.Refresh();
                tmpDir = Path.Combine(CacheDir(), task.Id);
                Directory.CreateDirectory(tmpDir);
                var tmpFile = Path.Combine(tmpDir, "v.mp4");
                var prog = new Progress<(long Done, long Total)>(p => { task.DownloadedBytes = p.Done; if (p.Total > 0) task.TotalBytes = p.Total; q.Refresh(); });
                await _dl.DownloadAsync(play.DurlUrl, tmpFile, prog, task.Cts.Token, backupUrls: null);
                var outFile = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.mp4");
                File.Move(tmpFile, outFile, true);
                task.OutputFile = outFile;
                Finish(q, "✅ 完成");
                return;
            }

            // DASH（登录高画质）
            var v = BiliClient.PickBestStream(play.VideoStreams, x => x.Id <= qn)
                 ?? BiliClient.PickBestStream(play.VideoStreams);
            var a = BiliClient.PickBestStream(play.AudioStreams);
            if (v == null) { Fail(q, "没有视频流"); return; }
            if (a == null && task.Mode != DownloadMode.AudioOnly) { Fail(q, "未获取到音频流"); return; }

            if (task.Mode == DownloadMode.AudioOnly)
            {
                task.StepText = "下载音频...";
                q.Refresh();
                tmpDir = Path.Combine(CacheDir(), task.Id);
                Directory.CreateDirectory(tmpDir);
                var tmpAF = Path.Combine(tmpDir, "a.m4s");
                task.TotalBytes = a!.Size;
                var dlA = new Downloader($"https://www.bilibili.com/video/{task.Bvid}/");
                await dlA.DownloadAsync(a.BaseUrl, tmpAF, new Progress<(long Done, long Total)>(p => { task.DownloadedBytes = p.Done; if (p.Total > 0) task.TotalBytes = p.Total; q.Refresh(); }), task.Cts.Token, backupUrls: a.BackupUrl);

                var fmt = string.IsNullOrEmpty(task.AudioFormat) ? "mp3" : task.AudioFormat;
                var outA = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}{AudioExt(fmt)}");

                // M4A：B站音频流本身就是 AAC/m4a 封装，无需转码，直接保存
                if (fmt == "m4a")
                {
                    File.Move(tmpAF, outA, true);
                    task.OutputFile = outA;
                    Finish(q, "✅ 完成");
                    return;
                }

                // 其余格式：需要 ffmpeg 转码；无ffmpeg时不弹窗，静默保留 m4a（可在顶栏ffmpeg按钮安装）
                task.StepText = $"转码{fmt.ToUpper()}...";
                q.Refresh();
                var ok = await Merger.ConvertAudio(tmpAF, outA, AudioCodecArgs(fmt));
                if (!ok)
                {
                    outA = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.m4a");
                    File.Move(tmpAF, outA, true);
                    task.OutputFile = outA;
                    Finish(q, "✅ 完成（无ffmpeg，已存m4a）");
                    return;
                }
                task.OutputFile = outA;
                Finish(q, "✅ 完成");
                return;
            }

            // 并行下音视频（带 Cookie + 备用URL + 视频页Referer）
            task.StepText = "下载中...";
            task.TotalBytes = v.Size + a!.Size;
            q.Refresh();
            tmpDir = Path.Combine(CacheDir(), task.Id);
            Directory.CreateDirectory(tmpDir);
            var vPath = Path.Combine(tmpDir, "v.m4s");
            var aPath = Path.Combine(tmpDir, "a.m4s");
            var dl = new Downloader($"https://www.bilibili.com/video/{task.Bvid}/");

            long vDone = 0, vTotal = 0, aDone = 0, aTotal = 0;
            var vt = dl.DownloadAsync(v.BaseUrl, vPath, new Progress<(long Done, long Total)>(p =>
            {
                Interlocked.Exchange(ref vDone, p.Done);
                if (p.Total > 0) Interlocked.Exchange(ref vTotal, p.Total);
                task.DownloadedBytes = Interlocked.Read(ref vDone) + Interlocked.Read(ref aDone);
                task.TotalBytes = Interlocked.Read(ref vTotal) + Interlocked.Read(ref aTotal);
                q.Refresh();
            }), task.Cts.Token, backupUrls: v.BackupUrl);
            var at = dl.DownloadAsync(a.BaseUrl, aPath, new Progress<(long Done, long Total)>(p =>
            {
                Interlocked.Exchange(ref aDone, p.Done);
                if (p.Total > 0) Interlocked.Exchange(ref aTotal, p.Total);
                task.DownloadedBytes = Interlocked.Read(ref vDone) + Interlocked.Read(ref aDone);
                task.TotalBytes = Interlocked.Read(ref vTotal) + Interlocked.Read(ref aTotal);
                q.Refresh();
            }), task.Cts.Token, backupUrls: a.BackupUrl);
            await Task.WhenAll(vt, at);

            // 合并
            task.StepText = "合并中...";
            q.Refresh();
            var outFile2 = Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.mp4");
            var merged = await Merger.MergeWithFfmpeg(vPath, aPath, outFile2);
            if (!merged)
            {
                // 不再弹窗：无ffmpeg时静默采用兼容模式拼接（可在顶栏ffmpeg按钮安装）
                task.StepText = "兼容模式拼接...";
                q.Refresh();
                merged = Merger.MergeConcat(vPath, aPath, outFile2);
            }
            if (!merged)
            {
                File.Move(vPath, Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.v.m4s"), true);
                File.Move(aPath, Path.Combine(task.OutputDir, $"{Sanitize(task.DisplayTitle)}.a.m4s"), true);
            }
            task.OutputFile = merged ? outFile2 : null;
            task.TotalBytes = task.DownloadedBytes;   // 合并阶段进度打满，不再显示0%
            q.Refresh();
            Finish(q, merged ? "✅ 完成" : "⚠ 合并失败，已保留m4s");
        }
        catch (Exception ex)
        {
            if (task.Status == "暂停中")   // 用户点击暂停触发的取消
            {
                task.Status = "已暂停";
                task.StepText = "已暂停";
                q.Refresh();
                Dispatcher.Invoke(() => UpdateAll());
                return;
            }
            task.Error = ex.Message;
            Fail(q, $"❌ {ex.Message}");
        }
        finally
        {
            CleanupTmp(tmpDir);   // 释放缓存：删除临时文件
            try { task.Cts?.Dispose(); task.Cts = null; } catch { }
        }
    }

    private void Finish(QueueItem q, string step)
    {
        q.Task.StepText = step;
        q.Task.Status = "完成";
        q.Task.DownloadedBytes = q.Task.TotalBytes;
        q.Refresh();
        Dispatcher.Invoke(() => { SetStatus($"完成: {q.Task.DisplayTitle}"); UpdateAll(); });
    }

    private void Fail(QueueItem q, string msg)
    {
        q.Task.StepText = msg;
        q.Task.Status = "失败";
        q.Refresh();
        Dispatcher.Invoke(() => { SetStatus(msg); UpdateAll(); });
    }

    private void UpdateAll()
    {
        TxtQueueCount.Text = $"{_queue.Count} 个任务";
        int done = _queue.Count(x => x.Task.Status == "完成");
        int total = _queue.Count(x => x.Task.Status != "等待中");
        long bytes = _queue.Sum(x => x.Task.DownloadedBytes);
        long all = _queue.Sum(x => x.Task.TotalBytes);
        ProgressAll.Value = all > 0 ? bytes * 100.0 / all : 0;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        // 移除所有不在下载中的任务（等待中/完成/失败），正在下载的保留
        var removable = _queue.Where(x => x.Task.Status != "下载中").ToList();
        if (removable.Count == 0)
        {
            SetStatus("所有任务都在下载中，请等待完成后再清空");
            return;
        }
        foreach (var r in removable) _queue.Remove(r);
        TxtQueueCount.Text = $"{_queue.Count} 个任务";
        TxtEmpty.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus($"已移除 {removable.Count} 个任务" + (_queue.Count > 0 ? "（下载中的任务已保留）" : ""));
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.CommandParameter is string id)
        {
            var item = _queue.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                _queue.Remove(item);
                TxtQueueCount.Text = $"{_queue.Count} 个任务";
                TxtEmpty.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ==================== 暂停 ====================

    private void BtnPause_Click(object sender, RoutedEventArgs e)
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

    private void BtnPauseAll_Click(object sender, RoutedEventArgs e)
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

    private void BtnBrowseCache_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择缓存文件夹（临时下载文件存放处，建议选空间充足的盘）" };
        if (dlg.ShowDialog() == true)
        {
            Config.Current.CacheDir = dlg.FolderName;
            Config.Save();
            SetStatus($"缓存目录: {dlg.FolderName}");
        }
    }

    private static string CacheDir()
    {
        var d = Config.Current.CacheDir;
        if (string.IsNullOrEmpty(d)) d = Path.Combine(Path.GetTempPath(), "BiliDownloader");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void CleanupTmp(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>顶栏ffmpeg按钮：查看/安装 ffmpeg</summary>
    private async void BtnFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var existing = Merger.FindFfmpeg();
        if (existing != null)
        {
            MessageBox.Show($"ffmpeg 已安装：\n{existing}", "ffmpeg", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = MessageBox.Show(
            "尚未安装 ffmpeg。\n\n" +
            "视频无损合并、音频转码（MP3/WAV/FLAC等）需要它。\n" +
            "没有它时：视频用兼容模式拼接，音频保存为M4A。\n\n" +
            "是否立即从国内镜像下载 ffmpeg？（约67MB，几秒完成）",
            "安装 ffmpeg", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (r != MessageBoxResult.Yes) return;
        SetStatus("正在下载 ffmpeg...");
        var (ff, err) = await Merger.DownloadFfmpeg();
        if (ff != null)
        {
            UpdateFfmpegButton();
            SetStatus("ffmpeg 安装成功");
            MessageBox.Show($"ffmpeg 安装成功！\n{ff}", "安装成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            SetStatus($"ffmpeg 安装失败: {err}");
            MessageBox.Show(
                $"ffmpeg 自动下载失败：{err}\n\n" +
                $"可手动下载 ffmpeg.exe 放到：\n{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDownloader", "ffmpeg")}",
                "安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateFfmpegButton()
    {
        BtnFfmpeg.Content = Merger.FindFfmpeg() != null ? "✓ ffmpeg" : "⬇ ffmpeg";
    }

    private static string AudioExt(string fmt) => fmt switch
    {
        "mp3" => ".mp3", "m4a" => ".m4a", "aac" => ".aac",
        "wav" => ".wav", "flac" => ".flac", "opus" => ".opus",
        _ => ".mp3"
    };

    private static string AudioCodecArgs(string fmt) => fmt switch
    {
        "mp3" => "-c:a libmp3lame -q:a 2 -id3v2_version 3",
        "aac" => "-c:a copy -f adts",
        "wav" => "-c:a pcm_s16le",
        "flac" => "-c:a flac",
        "opus" => "-c:a libopus -b:a 128k",
        _ => "-c:a libmp3lame -q:a 2 -id3v2_version 3"
    };

    private void SetStatus(string msg) => TxtStatus.Text = msg;

    // ==================== 登录 ====================

    private void BtnLogin_Click(object sender, RoutedEventArgs e)
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
        var r = MessageBox.Show("选择登录方式：\n\n是 - 扫码登录\n否 - 粘贴Cookie", "登录", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) _ = ShowQrAsync();
        else if (r == MessageBoxResult.No) PasteCookie();
    }

    private async Task ShowQrAsync()
    {
        // 先创建窗口并显示，再异步加载二维码（保证窗口一定弹出）
        var win = new Window { Title = "扫码登录", Width = 320, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = "请用B站App扫码", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
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
            var bmp = new WriteableBitmap(260, 260, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
            var px = new byte[260 * 260 * 4];
            for (int y = 0; y < 260; y++)
                for (int x = 0; x < 260; x++)
                {
                    int my = Math.Min((int)((double)y * n / 260), n - 1);
                    int mx = Math.Min((int)((double)x * n / 260), n - 1);
                    bool dark = matrix[my][mx];
                    int off = (y * 260 + x) * 4;
                    px[off] = px[off + 1] = px[off + 2] = dark ? (byte)0 : (byte)255;
                    px[off + 3] = 255;
                }
            bmp.WritePixels(new Int32Rect(0, 0, 260, 260), px, 260 * 4, 0);
            sp.Children.Insert(1, new Image { Source = bmp, Width = 260, Height = 260 });
            status.Text = "等待扫码...";

            var cts = new CancellationTokenSource();
            win.Closed += (_, _) => cts.Cancel();
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var (code, redirect) = await BiliClient.PollQr(key);
                    await Dispatcher.InvokeAsync(() => status.Text = code switch
                    {
                        86101 => "等待扫码...", 86090 => "已扫码，请在手机上确认", 0 => "登录成功！", _ => "已过期"
                    });
                    if (code == 0 && redirect != null)
                    {
                        var info = await BiliClient.GetLogin(redirect);
                        if (info != null)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                Config.Current.Login = info; Config.Save(); BiliClient.ApplyLoginCookie(); UpdateLoginUI(); win.Close();
                            });
                        }
                        else
                        {
                            await Dispatcher.InvokeAsync(() => status.Text = "提取Cookie失败，请改用粘贴Cookie");
                        }
                        break;
                    }
                    if (code == 86038) break;
                    await Task.Delay(2000, cts.Token);
                }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => status.Text = $"二维码加载失败: {ex.Message}\n请改用粘贴Cookie");
            SetStatus($"登录失败: {ex.Message}");
        }
    }

    private void PasteCookie()
    {
        var win = new Window { Title = "粘贴Cookie", Width = 400, Height = 180, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var sp = new StackPanel { Margin = new Thickness(16) };
        var tb = new TextBox { Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(new TextBlock { Text = "粘贴 SESSDATA 或完整Cookie:", Margin = new Thickness(0, 0, 0, 8) });
        sp.Children.Add(tb);
        var ok = new Button { Content = "确认", Width = 70, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { win.DialogResult = true; win.Close(); };
        sp.Children.Add(ok);
        win.Content = sp;
        if (win.ShowDialog() == true)
        {
            var info = BiliClient.ParseCookie(tb.Text.Trim());
            if (info != null)
            {
                Config.Current.Login = info; Config.Save(); BiliClient.ApplyLoginCookie(); UpdateLoginUI();
                SetStatus("Cookie已保存");
            }
            else SetStatus("无法解析Cookie");
        }
    }

    // ==================== 辅助 ====================

    private static string FormatDur(int s)
    {
        var t = TimeSpan.FromSeconds(s);
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private static string Sanitize(string name)
    {
        var inv = Path.GetInvalidFileNameChars();
        var c = new string(name.Select(x => inv.Contains(x) ? '_' : x).ToArray());
        return c.Length > 80 ? c[..80] : c;
    }
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
