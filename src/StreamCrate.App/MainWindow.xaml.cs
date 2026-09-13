using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StreamCrate.App.Presentation;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Diagnostics;
using StreamCrate.Infrastructure.Processes;
using StreamCrate.Infrastructure.Queue;
using StreamCrate.Infrastructure.Storage;
using StreamCrate.Infrastructure.Tooling;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace StreamCrate.App;

public sealed partial class MainWindow : Window
{
    private readonly ToolchainManager _toolchain = new(new HttpClient());
    private readonly DownloadQueueService _queue;
    private readonly IHistoryStore _history;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ObservableCollection<QueueItem> _inProgressQueueItems = [];
    private readonly ObservableCollection<QueueItem> _completedQueueItems = [];
    private readonly ObservableCollection<QueueItem> _failedQueueItems = [];
    private readonly Dictionary<Guid, QueueItem> _queueItemsById = [];
    private readonly ObservableCollection<HistoryDayGroup> _historyGroups = [];
    private readonly HashSet<Guid> _storedHistoryIds = [];
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _toolsReady;
    private bool _isInitializing;
    private bool _isProbing;
    private MediaItem? _probedMedia;
    private PlaylistSelection? _playlistSelection;
    private CookieSelection _probedCookies = CookieSelection.None;
    private readonly UISettings _uiSettings = new();
    private string? _backgroundImagePath;
    private string _selectedNavTag = "download";
    private readonly List<double> _throughputSamples = [];
    private const int ThroughputSampleCount = 24;
    // yt-dlp reports progress several times a second. Feeding every report straight into the
    // readout and the sparkline made both jitter, so the rate is latched here and sampled on a
    // steady one-second beat instead; the per-job progress bars still update on every report.
    private readonly DispatcherTimer _throughputTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private double _latestThroughput;
    // Starts true so the settings controls' change events, which fire while XAML is still
    // loading (MotionTempoBox carries SelectedIndex="1"), cannot touch controls that do not
    // exist yet. ApplySettings clears it once the window is fully built.
    private bool _isApplyingSettings = true;
    private const float NavItemHeight = 43f;
    private SegmentedSelector _formatSegments = null!;
    private SegmentedSelector _qualitySegments = null!;
    private SegmentedSelector _defaultFormatSegments = null!;
    private SegmentedSelector _defaultQualitySegments = null!;
    private SegmentedSelector _themeSegments = null!;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamCrate");
        _settingsStore = new JsonAppSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        _history = new SqliteHistoryStore(Path.Combine(dataDirectory, "history.db"));
        _queue = new DownloadQueueService(ExecuteDownloadAsync);
        _queue.JobChanged += QueueJobChanged;
        _throughputTimer.Tick += ThroughputTimerTick;
        BuildSegmentedSelectors();
        InProgressQueueList.ItemsSource = _inProgressQueueItems;
        CompletedQueueList.ItemsSource = _completedQueueItems;
        FailedQueueList.ItemsSource = _failedQueueItems;
        HistoryList.ItemsSource = _historyGroups;
        Activated += InitializeNavOnFirstActivation;
    }

    private void InitializeNavOnFirstActivation(object sender, WindowActivatedEventArgs args)
    {
        Activated -= InitializeNavOnFirstActivation;
        UpdateNavItemVisuals();
        MoveNavIndicator(NavIndexOf(_selectedNavTag), animate: false);
        _ = PlayPageEntranceAsync(_selectedNavTag);
    }

    public async Task InitializeAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            ApplySettings();
        }
        catch (Exception exception)
        {
            ShowError("無法載入設定", exception.Message);
        }

        await EnsureToolsAsync();
    }

    private async Task EnsureToolsAsync()
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;
        ToolProgressRing.Visibility = Visibility.Visible;
        ToolProgressRing.IsActive = true;
        ToolStatusText.Text = "正在準備下載工具";
        RetryToolsButton.Visibility = Visibility.Collapsed;
        ProbeButton.IsEnabled = false;
        try
        {
            await _toolchain.EnsureAvailableAsync();
            _toolsReady = true;
            ToolStatusText.Text = "下載工具已準備完成";
            StatusBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            _toolsReady = false;
            ToolStatusText.Text = "下載工具尚未準備完成";
            ShowStatus("工具準備失敗", exception.Message, InfoBarSeverity.Error, true);
        }
        finally
        {
            _isInitializing = false;
            ToolProgressRing.IsActive = false;
            ToolProgressRing.Visibility = Visibility.Collapsed;
            ProbeButton.IsEnabled = _toolsReady && !_isProbing;
        }
    }

    private async void RetryToolsClicked(object sender, RoutedEventArgs args) => await EnsureToolsAsync();

    private async void NavItemClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
        {
            await SelectNavAsync(tag);
        }
    }

    private async Task SelectNavAsync(string tag)
    {
        _selectedNavTag = tag;
        UpdateNavItemVisuals();
        MoveNavIndicator(NavIndexOf(tag));
        await ShowPanelAsync(tag);
    }

    private IReadOnlyList<Button> NavButtons => NavItemsPanel.Children.OfType<Button>().ToArray();

    private int NavIndexOf(string tag)
    {
        var buttons = NavButtons;
        for (var index = 0; index < buttons.Count; index++)
        {
            if (Equals(buttons[index].Tag, tag))
            {
                return index;
            }
        }

        return 0;
    }

    private void UpdateNavItemVisuals()
    {
        foreach (var button in NavButtons)
        {
            var selected = Equals(button.Tag, _selectedNavTag);
            button.Style = (Style)Application.Current.Resources[selected ? "NavItemSelectedButtonStyle" : "NavItemButtonStyle"];
        }
    }

    private void NavItemsPanelSizeChanged(object sender, SizeChangedEventArgs args)
    {
        NavIndicator.Width = Math.Max(args.NewSize.Width, 0);
        MoveNavIndicator(NavIndexOf(_selectedNavTag), animate: false);
    }

    private void BuildSegmentedSelectors()
    {
        _formatSegments = new SegmentedSelector(SegmentedSelectorKind.Accent, FormatMp4Button, FormatMp3Button);
        _qualitySegments = new SegmentedSelector(
            SegmentedSelectorKind.Chip,
            QualityBestButton,
            Quality2160Button,
            Quality1440Button,
            Quality1080Button,
            Quality720Button);
        _defaultFormatSegments = new SegmentedSelector(SegmentedSelectorKind.Accent, DefaultFormatMp4Button, DefaultFormatMp3Button);
        _defaultQualitySegments = new SegmentedSelector(
            SegmentedSelectorKind.Chip,
            DefaultQualityBestButton,
            DefaultQuality2160Button,
            DefaultQuality1440Button,
            DefaultQuality1080Button,
            DefaultQuality720Button);
        _themeSegments = new SegmentedSelector(SegmentedSelectorKind.Neutral, ThemeDarkButton, ThemeLightButton);

        _formatSegments.SelectedIndex = 0;
        _qualitySegments.SelectedIndex = 0;
        _defaultFormatSegments.SelectedIndex = 0;
        _defaultQualitySegments.SelectedIndex = 0;
        _themeSegments.SelectedIndex = 0;

        _defaultFormatSegments.SelectionChanged += SettingsSegmentChanged;
        _defaultQualitySegments.SelectionChanged += SettingsSegmentChanged;
        _themeSegments.SelectionChanged += SettingsSegmentChanged;
    }

    private void SettingsSegmentChanged(object? sender, EventArgs args) => UpdateSettingsDirtyState();

    private void MoveNavIndicator(int index, bool animate = true)
    {
        var target = index * NavItemHeight;
        ElementCompositionPreview.SetIsTranslationEnabled(NavIndicator, true);
        var visual = ElementCompositionPreview.GetElementVisual(NavIndicator);
        visual.StopAnimation("Translation");
        if (!animate || !_uiSettings.AnimationsEnabled)
        {
            visual.Properties.InsertVector3("Translation", new Vector3(0, target, 0));
            NavIndicator.Opacity = 1;
            return;
        }

        NavIndicator.Opacity = 1;
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.12f, 0.9f), new Vector2(0.18f, 1));
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new Vector3(0, target, 0), easing);
        animation.Duration = MotionTime(340);
        visual.StartAnimation("Translation", animation);
    }

    private async Task ShowPanelAsync(string? tag)
    {
        DownloadPanel.Visibility = tag == "download" ? Visibility.Visible : Visibility.Collapsed;
        QueuePanel.Visibility = tag == "queue" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "queue")
        {
            RenderQueue();
        }
        else if (tag == "history")
        {
            await RenderHistoryAsync();
        }

        await PlayPageEntranceAsync(tag);
    }

    private async void ProbeClicked(object sender, RoutedEventArgs args)
    {
        if (_isProbing || !_toolsReady)
        {
            return;
        }

        if (!Uri.TryCreate(UrlBox.Text?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ShowError("無效網址", "請輸入完整的 http 或 https 網址。");
            return;
        }

        var cookies = await GetCookieSelectionAsync();
        if (cookies is null)
        {
            return;
        }

        _isProbing = true;
        ProbeButton.IsEnabled = false;
        ProbeBusyPanel.Visibility = Visibility.Visible;
        ClearResolvedResult();
        try
        {
            var result = await new YtDlpMediaProbeService(_toolchain.YtDlpPath).ProbeAsync(uri, cookies);
            _probedCookies = cookies;
            if (result.IsPlaylist)
            {
                ShowPlaylistResult(result.Playlist!);
            }
            else
            {
                ShowSingleMediaResult(result.Media!);
            }

            StatusBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            ClearResolvedResult();
            ShowError("解析失敗", UserFacingErrorMapper.Map(exception.Message));
        }
        finally
        {
            _isProbing = false;
            ProbeBusyPanel.Visibility = Visibility.Collapsed;
            ProbeButton.IsEnabled = _toolsReady;
        }
    }

    private void ShowSingleMediaResult(MediaItem media)
    {
        _probedMedia = media;
        _playlistSelection = null;
        ResultTitleText.Text = media.Title;
        ResultMetaText.Text = $"{media.Extractor} · {FormatDuration(media.Duration)}";
        SingleResultPanel.Visibility = Visibility.Visible;
        PlaylistResultPanel.Visibility = Visibility.Collapsed;
        ShowResultThumbnail(media.ThumbnailUrl);
        QueueButton.Content = "加入下載佇列";
        QueueButton.IsEnabled = true;
        ResolvedResultPanel.Visibility = Visibility.Visible;
    }

    private void ShowPlaylistResult(PlaylistInfo playlist)
    {
        _probedMedia = null;
        _playlistSelection = new PlaylistSelection(playlist);
        _playlistSelection.SelectedCountChanged += PlaylistSelectionChanged;
        PlaylistItems.ItemsSource = _playlistSelection.Items;
        PlaylistTitleText.Text = playlist.Title;
        PlaylistCountText.Text = $"共找到 {playlist.Items.Count} 部影片，可依需要取消勾選。";
        SingleResultPanel.Visibility = Visibility.Collapsed;
        PlaylistResultPanel.Visibility = Visibility.Visible;
        // A playlist result stands for many videos, so the tile shows the first item's still.
        ShowResultThumbnail(playlist.Items.FirstOrDefault()?.ThumbnailUrl);
        UpdatePlaylistSelectionSummary();
        ResolvedResultPanel.Visibility = Visibility.Visible;
    }

    private void ShowResultThumbnail(Uri? thumbnailUrl)
    {
        var source = Thumbnails.Load(thumbnailUrl, ResultThumbnailWidth);
        ResultThumbnail.Source = source;
        ResultThumbnail.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private const int ResultThumbnailWidth = 176;

    private void PlaylistSelectionChanged(object? sender, EventArgs args) => UpdatePlaylistSelectionSummary();

    private void UpdatePlaylistSelectionSummary()
    {
        if (_playlistSelection is null)
        {
            return;
        }

        var selectedCount = _playlistSelection.SelectedCount;
        SelectedCountText.Text = $"已選取 {selectedCount} / {_playlistSelection.Items.Count}";
        QueueButton.Content = selectedCount == 0 ? "請先選取影片" : $"將選取的 {selectedCount} 部加入佇列";
        QueueButton.IsEnabled = selectedCount > 0;
    }

    private void SelectAllPlaylistClicked(object sender, RoutedEventArgs args) => _playlistSelection?.SetAllSelected(true);

    private void ClearPlaylistSelectionClicked(object sender, RoutedEventArgs args) => _playlistSelection?.SetAllSelected(false);

    private async void QueueClicked(object sender, RoutedEventArgs args)
    {
        var requests = CreateQueuedRequests();
        if (requests.Count == 0)
        {
            ShowStatus("尚未選取影片", "請至少選取一部影片再加入佇列。", InfoBarSeverity.Warning, false);
            return;
        }

        var addedCount = 0;
        foreach (var request in requests)
        {
            await _queue.EnqueueAsync(request);
            addedCount++;
        }

        QueueFeedbackText.Text = $"已將 {addedCount} 部影片加入下載佇列。";
        ViewQueueButton.Visibility = Visibility.Visible;
        RenderQueue();
    }

    private IReadOnlyList<DownloadRequest> CreateQueuedRequests()
    {
        var format = _formatSegments.SelectedIndex == 1 ? DownloadFormat.Mp3 : DownloadFormat.Mp4;
        var quality = (VideoQuality)Math.Max(_qualitySegments.SelectedIndex, 0);
        if (_playlistSelection is not null)
        {
            return _playlistSelection.CreateRequests(_settings.DownloadDirectory, format, quality, _probedCookies);
        }

        return _probedMedia is null
            ? []
            : [new DownloadRequest(_probedMedia, _settings.DownloadDirectory, format, quality, _probedCookies)];
    }

    private void ViewQueueClicked(object sender, RoutedEventArgs args) => _ = SelectNavAsync("queue");

    private void UrlTextChanged(object sender, TextChangedEventArgs args)
    {
        if (ResolvedResultPanel.Visibility == Visibility.Visible)
        {
            ClearResolvedResult();
        }
    }

    private void ClearResolvedResult()
    {
        if (_playlistSelection is not null)
        {
            _playlistSelection.SelectedCountChanged -= PlaylistSelectionChanged;
        }

        _probedMedia = null;
        _playlistSelection = null;
        PlaylistItems.ItemsSource = null;
        QueueButton.IsEnabled = false;
        QueueFeedbackText.Text = string.Empty;
        ViewQueueButton.Visibility = Visibility.Collapsed;
        ResolvedResultPanel.Visibility = Visibility.Collapsed;
    }

    private async Task<CookieSelection?> GetCookieSelectionAsync()
    {
        var source = (CookieBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (source != "file")
        {
            return source switch
            {
                "chrome" => new CookieSelection(CookieSource.Chrome),
                "edge" => new CookieSelection(CookieSource.Edge),
                "firefox" => new CookieSelection(CookieSource.Firefox),
                _ => CookieSelection.None,
            };
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".txt");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            ShowStatus("未選取 cookies.txt", "請選取僅供本次使用的 Netscape cookies.txt 檔案。", InfoBarSeverity.Warning, false);
            return null;
        }

        return new CookieSelection(CookieSource.CookiesFile, file.Path);
    }

    private async void ChooseBackgroundImageClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _backgroundImagePath = file.Path;
        ApplyBackgroundImage(_backgroundImagePath);
        SettingsMessage.Text = "背景圖片已預覽；按「儲存設定」後會保留這項偏好。";
        UpdateSettingsDirtyState();
    }

    private void ClearBackgroundImageClicked(object sender, RoutedEventArgs args)
    {
        _backgroundImagePath = null;
        ApplyBackgroundImage(null);
        SettingsMessage.Text = "背景圖片已清除預覽；按「儲存設定」後會保留這項偏好。";
        UpdateSettingsDirtyState();
    }

    private void ApplyBackgroundImage(string? path)
    {
        var resolvedPath = BackgroundImagePathResolver.Resolve(path);
        if (resolvedPath is null)
        {
            BackgroundImage.Source = null;
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundImagePathText.Text = string.IsNullOrWhiteSpace(path)
                ? "未使用自訂背景。"
                : "背景圖片目前無法使用，已改用主題背景。";
            return;
        }

        try
        {
            BackgroundImage.Source = new BitmapImage(new Uri(resolvedPath));
            BackgroundImage.Visibility = Visibility.Visible;
            BackgroundImagePathText.Text = $"目前背景：{Path.GetFileName(resolvedPath)}";
        }
        catch (Exception)
        {
            BackgroundImage.Source = null;
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundImagePathText.Text = "背景圖片目前無法使用，已改用主題背景。";
        }
    }

    private void BackgroundImageFailed(object sender, ExceptionRoutedEventArgs args)
    {
        BackgroundImage.Source = null;
        BackgroundImage.Visibility = Visibility.Collapsed;
        BackgroundImagePathText.Text = "背景圖片無法載入，已改用主題背景。";
    }

    private async Task ExecuteDownloadAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var specification = new YtDlpCommandFactory().CreateDownload(job.Request, Path.GetDirectoryName(_toolchain.FfmpegPath)!);
        var runner = new YtDlpProcessRunner();
        var executableSpecification = specification with { ExecutablePath = _toolchain.YtDlpPath };
        try
        {
            await runner.RunAsync(executableSpecification, progress, cancellationToken);
        }
        catch (InvalidOperationException exception) when (YouTube403RetryPolicy.ShouldRetry(job.Request.Media.Extractor, exception.Message))
        {
            await runner.RunAsync(YouTube403RetryPolicy.WithForceIpv4(executableSpecification), progress, cancellationToken);
        }
    }

    private void QueueJobChanged(object? sender, DownloadJob job) => DispatcherQueue.TryEnqueue(() => _ = HandleQueueJobChangedAsync(job));

    private async Task HandleQueueJobChangedAsync(DownloadJob job)
    {
        try
        {
        var shouldStoreHistory = job.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled;
        if (shouldStoreHistory && _storedHistoryIds.Add(job.Id))
            {
                await _history.SaveAsync(new HistoryEntry(
                    job.Id,
                    job.Request.Media.Extractor,
                    job.Request.Media.MediaId,
                    job.Request.Media.Title,
                    job.Request.Media.SourceUrl,
                    job.Request.Format,
                    job.Request.Quality,
                    job.Request.OutputDirectory,
                    job.State,
                    job.ErrorCategory,
                    job.CreatedAt,
                    job.Request.Media.ThumbnailUrl));
            }

            RenderQueue();
        }
        catch (Exception exception)
        {
            ShowError("無法保存歷史紀錄", exception.Message);
        }
    }

    private async void RetryJobClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not Guid jobId
            || !_queueItemsById.TryGetValue(jobId, out var item))
        {
            return;
        }

        await _queue.EnqueueAsync(item.Request);
        RenderQueue();
    }

    private void CopyJobErrorClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not string message || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(message);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void CancelJobClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is Guid jobId)
        {
            _queue.Cancel(jobId);
        }
    }

    private void RenderQueue()
    {
        var jobs = _queue.GetSnapshot();
        var activeIds = jobs.Select(job => job.Id).ToHashSet();
        foreach (var removedId in _queueItemsById.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _queueItemsById.Remove(removedId);
        }

        foreach (var job in jobs)
        {
            if (_queueItemsById.TryGetValue(job.Id, out var item))
            {
                item.Update(job);
            }
            else
            {
                _queueItemsById[job.Id] = new QueueItem(job);
            }
        }

        QueueSectionSynchronizer.Synchronize(_inProgressQueueItems, jobs.Where(job => QueueItem.GetSection(job.State) == "InProgress").Select(job => _queueItemsById[job.Id]));
        QueueSectionSynchronizer.Synchronize(_completedQueueItems, jobs.Where(job => QueueItem.GetSection(job.State) == "Completed").Select(job => _queueItemsById[job.Id]));
        QueueSectionSynchronizer.Synchronize(_failedQueueItems, jobs.Where(job => QueueItem.GetSection(job.State) == "Failed").Select(job => _queueItemsById[job.Id]));

        var activeCount = jobs.Count(job => job.State is DownloadJobState.Queued or DownloadJobState.Probing or DownloadJobState.Downloading or DownloadJobState.PostProcessing);
        QueueSummaryText.Text = $"QUEUE · {activeCount} ACTIVE";
        UpdateThroughput(jobs);
        InProgressCountText.Text = _inProgressQueueItems.Count.ToString();
        CompletedCountText.Text = _completedQueueItems.Count.ToString();
        FailedCountText.Text = _failedQueueItems.Count.ToString();
        QueueCountBadgeText.Text = activeCount.ToString();
        QueueCountBadge.Visibility = activeCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The design keeps the board as three standing columns; only a completely empty queue
        // swaps in the placeholder, so a single job no longer collapses the layout to one column.
        var hasJobs = jobs.Count > 0;
        QueueEmptyState.Visibility = hasJobs ? Visibility.Collapsed : Visibility.Visible;
        var sectionVisibility = hasJobs ? Visibility.Visible : Visibility.Collapsed;
        InProgressQueueSection.Visibility = sectionVisibility;
        CompletedQueueSection.Visibility = sectionVisibility;
        FailedQueueSection.Visibility = sectionVisibility;
    }

    private void UpdateThroughput(IReadOnlyList<DownloadJob> jobs)
    {
        _latestThroughput = jobs
            .Where(job => job.State == DownloadJobState.Downloading)
            .Sum(job => TransferRate.ToMegabytesPerSecond(job.Progress?.Speed));

        if (_latestThroughput > 0 || jobs.Any(job => job.State == DownloadJobState.Downloading))
        {
            if (!_throughputTimer.IsEnabled)
            {
                _throughputTimer.Start();
                SampleThroughput();
            }

            return;
        }

        if (_throughputTimer.IsEnabled)
        {
            _throughputTimer.Stop();
            _latestThroughput = 0;
            SampleThroughput();
        }
    }

    private void ThroughputTimerTick(object? sender, object args) => SampleThroughput();

    private void SampleThroughput()
    {
        QueueThroughputText.Text = _latestThroughput.ToString(_latestThroughput >= 100 ? "0" : "0.0");
        _throughputSamples.Add(_latestThroughput);
        while (_throughputSamples.Count > ThroughputSampleCount)
        {
            _throughputSamples.RemoveAt(0);
        }

        RenderSparkline();
    }

    private void QueueSparklineSizeChanged(object sender, SizeChangedEventArgs args) => RenderSparkline();

    private void RenderSparkline()
    {
        var width = QueueSparklineHost.ActualWidth;
        var height = QueueSparklineHost.ActualHeight;
        var points = TransferRate.BuildSparkline(_throughputSamples, width, height);
        QueueSparkline.Points.Clear();
        QueueSparklineFill.Points.Clear();
        if (points.Count == 0)
        {
            return;
        }

        foreach (var (x, y) in points)
        {
            QueueSparkline.Points.Add(new Windows.Foundation.Point(x, y));
            QueueSparklineFill.Points.Add(new Windows.Foundation.Point(x, y));
        }

        QueueSparklineFill.Points.Add(new Windows.Foundation.Point(points[^1].X, height));
        QueueSparklineFill.Points.Add(new Windows.Foundation.Point(points[0].X, height));
    }

    private async Task RenderHistoryAsync()
    {
        try
        {
            var entries = await _history.SearchAsync(HistorySearchBox.Text, null);
            _historyGroups.Clear();
            foreach (var group in HistoryDayGroup.Build(entries, DateTime.Today))
            {
                _historyGroups.Add(group);
            }

            var localEntries = entries.Select(entry => (Entry: entry, LocalTime: entry.CreatedAt.LocalDateTime)).ToArray();
            var today = DateTime.Today;
            var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
            var weekStart = today.AddDays(-daysSinceMonday);
            var successCount = entries.Count(entry => entry.State is DownloadJobState.Completed or DownloadJobState.SkippedExisting);
            HistoryTotalText.Text = entries.Count.ToString();
            HistoryWeekText.Text = localEntries.Count(item => item.LocalTime >= weekStart).ToString();
            HistorySuccessRateText.Text = entries.Count == 0
                ? "—"
                : $"{Math.Round(successCount * 100d / entries.Count):0}%";
            HistoryEmptyState.Visibility = _historyGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            await UpdateHistoryStorageAsync(entries);
        }
        catch (Exception exception)
        {
            ShowError("無法讀取歷史紀錄", exception.Message);
        }
    }

    /// <summary>
    /// Sums what the kept downloads still occupy on disk. Files the user has since moved or deleted
    /// simply count as zero, so the metric tracks real usage rather than what history remembers.
    /// </summary>
    private async Task UpdateHistoryStorageAsync(IReadOnlyList<HistoryEntry> entries)
    {
        var paths = entries.Select(entry => entry.OutputPath).ToArray();
        var bytes = await Task.Run(() => paths.Sum(HistoryItem.SizeOnDisk));
        var megabytes = bytes / 1024d / 1024d;
        if (megabytes >= 1024)
        {
            HistoryStorageText.Text = $"{megabytes / 1024:0.#}";
            HistoryStorageUnitText.Text = "GB";
        }
        else
        {
            HistoryStorageText.Text = $"{megabytes:0.#}";
            HistoryStorageUnitText.Text = "MB";
        }
    }

    private void HistorySearchChanged(object sender, TextChangedEventArgs args) => _ = RenderHistoryAsync();

    private async void ClearHistoryClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "清除全部歷史紀錄？",
            Content = "這會移除本機保存的下載結果，無法復原。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _history.ClearAsync();
            await RenderHistoryAsync();
        }
    }

    private async void RetryHistoryEntryClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not string sourceUrl || string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        UrlBox.Text = sourceUrl;
        await SelectNavAsync("download");
    }

    private async void OpenHistoryFolderClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string outputPath)
        {
            await OpenFolderAsync(outputPath);
        }
    }

    private async void OpenQueueFolderClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string outputPath)
        {
            await OpenFolderAsync(outputPath);
        }
    }

    private async Task OpenFolderAsync(string outputPath)
    {
        if (!Directory.Exists(outputPath) || !await Windows.System.Launcher.LaunchFolderPathAsync(outputPath))
        {
            ShowError("無法開啟檔案位置", "輸出資料夾不存在或 Windows 無法開啟它。");
        }
    }

    private async void ChooseDownloadFolderClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            DownloadFolderBox.Text = folder.Path;
            SettingsMessage.Text = "資料夾已選擇，請儲存設定後套用。";
        }
    }

    private async void SaveSettingsClicked(object sender, RoutedEventArgs args)
    {
        var directory = DownloadFolderBox.Text.Trim();
        if (!Directory.Exists(directory))
        {
            SettingsMessage.Text = "請輸入已存在的下載資料夾。";
            return;
        }

        _settings = new AppSettings(
            directory,
            _defaultFormatSegments.SelectedIndex == 1 ? DownloadFormat.Mp3 : DownloadFormat.Mp4,
            (VideoQuality)Math.Max(_defaultQualitySegments.SelectedIndex, 0),
            _themeSegments.SelectedIndex == 1 ? AppTheme.Light : AppTheme.Dark,
            _backgroundImagePath,
            MotionTempoBox.SelectedIndex switch
            {
                0 => 0.75,
                2 => 1.25,
                _ => 1.0,
            });
        await _settingsStore.SaveAsync(_settings);
        ApplySettings();
        SettingsMessage.Text = "設定已保存，之後的下載會使用新預設。";
    }

    private void SettingsSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSettingsDirtyState();

    private void SettingsTextChanged(object sender, TextChangedEventArgs args) => UpdateSettingsDirtyState();

    private int CountUnsavedSettings()
    {
        var changes = 0;
        if (!string.Equals(DownloadFolderBox.Text.Trim(), _settings.DownloadDirectory, StringComparison.OrdinalIgnoreCase))
        {
            changes++;
        }

        if (_defaultFormatSegments.SelectedIndex != (_settings.DefaultFormat == DownloadFormat.Mp3 ? 1 : 0))
        {
            changes++;
        }

        if (_defaultQualitySegments.SelectedIndex != (int)_settings.DefaultQuality)
        {
            changes++;
        }

        if (_themeSegments.SelectedIndex != (_settings.Theme == AppTheme.Light ? 1 : 0))
        {
            changes++;
        }

        if (MotionTempoBox.SelectedIndex != MotionTempoIndex(_settings.MotionTempo))
        {
            changes++;
        }

        if (!string.Equals(_backgroundImagePath, _settings.BackgroundImagePath, StringComparison.OrdinalIgnoreCase))
        {
            changes++;
        }

        return changes;
    }

    private static int MotionTempoIndex(double tempo) => tempo switch
    {
        <= 0.85 => 0,
        >= 1.15 => 2,
        _ => 1,
    };

    private void UpdateSettingsDirtyState()
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var changes = CountUnsavedSettings();
        SettingsDirtyText.Text = changes == 0 ? "所有變更都已儲存。" : $"有 {changes} 項未儲存的變更";
    }

    private void ApplySettings()
    {
        _isApplyingSettings = true;
        try
        {
            ApplySettingsCore();
        }
        finally
        {
            _isApplyingSettings = false;
        }

        UpdateSettingsDirtyState();
    }

    private void ApplySettingsCore()
    {
        DownloadFolderBox.Text = _settings.DownloadDirectory;
        _defaultFormatSegments.SelectedIndex = _settings.DefaultFormat == DownloadFormat.Mp3 ? 1 : 0;
        _defaultQualitySegments.SelectedIndex = (int)_settings.DefaultQuality;
        _themeSegments.SelectedIndex = _settings.Theme == AppTheme.Light ? 1 : 0;
        MotionTempoBox.SelectedIndex = _settings.MotionTempo switch
        {
            <= 0.85 => 0,
            >= 1.15 => 2,
            _ => 1,
        };
        _formatSegments.SelectedIndex = _defaultFormatSegments.SelectedIndex;
        _qualitySegments.SelectedIndex = _defaultQualitySegments.SelectedIndex;
        RootGrid.RequestedTheme = _settings.Theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        _backgroundImagePath = _settings.BackgroundImagePath;
        ApplyBackgroundImage(_backgroundImagePath);
        UpdateStorageSummary();
        StartAmbientAnimation();
    }

    private void UpdateStorageSummary()
    {
        try
        {
            var fullPath = Path.GetFullPath(_settings.DownloadDirectory);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("找不到下載資料夾所在磁碟。");
            }

            var drive = new DriveInfo(root);
            var freeGigabytes = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            var usedPercent = drive.TotalSize == 0
                ? 0
                : (drive.TotalSize - drive.AvailableFreeSpace) * 100d / drive.TotalSize;
            StorageRemainingText.Text = freeGigabytes >= 100
                ? Math.Floor(freeGigabytes).ToString("0")
                : freeGigabytes.ToString("0.0");
            StorageUsageBar.Value = Math.Clamp(usedPercent, 0, 100);
        }
        catch (Exception)
        {
            StorageRemainingText.Text = "—";
            StorageUsageBar.Value = 0;
        }
    }

    private async Task PlayPageEntranceAsync(string? tag)
    {
        await Task.Yield();
        var (title, subtitle, content) = GetPageEntranceElements(tag);
        if (!_uiSettings.AnimationsEnabled)
        {
            SetImmediatelyVisible(title);
            SetImmediatelyVisible(subtitle);
            foreach (var element in content)
            {
                SetImmediatelyVisible(element);
            }

            SetItemContainersImmediatelyVisible(tag);
            return;
        }

        AnimatePageTitle(title, 0);
        AnimateOpacity(subtitle, 60);
        var delay = 120;
        foreach (var element in content)
        {
            if (element.Visibility == Visibility.Visible)
            {
                if (tag == "settings")
                {
                    AnimateSlideFromRight(element, delay, 22, 400);
                }
                else
                {
                    AnimateSlideUp(element, delay);
                }

                delay += 55;
            }
        }

        await AnimateItemContainersAsync(tag, delay);
    }

    private (UIElement Title, UIElement Subtitle, IReadOnlyList<UIElement> Content) GetPageEntranceElements(string? tag) => tag switch
    {
        "queue" => (QueuePageTitle, QueuePageSubtitle, [QueueBoard]),
        "history" => (HistoryPageTitle, HistoryPageSubtitle, [HistoryControls, HistoryStatsPanel, HistoryEmptyState]),
        "settings" => (SettingsPageTitle, SettingsPageSubtitle, [DownloadDefaultsCard, AppearanceCard, SettingsSavePanel]),
        _ => (DownloadPageTitle, DownloadPageSubtitle, [DownloadPrimaryCard, ResolvedResultPanel]),
    };

    private async Task AnimateItemContainersAsync(string? tag, int delay)
    {
        if (tag == "history")
        {
            await AnimateHistoryRowsAsync(delay);
            return;
        }

        var lists = GetAnimatedItemLists(tag);
        if (PageEntranceAnimationScheduler.RequiresLayoutPass(GetItemCount(lists), GetRealizedContainerCount(lists)))
        {
            await WaitForNextLayoutAsync();
        }

        foreach (var list in lists)
        {
            foreach (var item in list.Items)
            {
                if (list.ContainerFromItem(item) is UIElement container)
                {
                    AnimateSlideFromRight(container, delay, 44, 440);
                    delay += 55;
                }
            }
        }
    }

    /// <summary>
    /// Slides the history entries in one at a time from the top down. HistoryList's own items are
    /// day groups, so animating its containers would move a whole day as one block; the rows live
    /// in a nested ItemsControl inside each group's template and are reached through the visual
    /// tree, with the stagger running continuously across day boundaries.
    /// </summary>
    private async Task AnimateHistoryRowsAsync(int delay)
    {
        if (HistoryRowContainersArePending())
        {
            await WaitForNextLayoutAsync();
        }

        foreach (var group in HistoryList.Items)
        {
            if (HistoryList.ContainerFromItem(group) is not DependencyObject groupContainer)
            {
                continue;
            }

            if (groupContainer is UIElement groupElement)
            {
                // The group itself only carries the rows; it must not fade as a block.
                SetImmediatelyVisible(groupElement);
            }

            var (header, rows) = FindHistoryGroupParts(groupContainer);
            if (header is not null)
            {
                AnimateOpacity(header, delay);
            }

            if (rows is null)
            {
                continue;
            }

            foreach (var item in rows.Items)
            {
                if (rows.ContainerFromItem(item) is UIElement row)
                {
                    AnimateSlideFromRight(row, delay, 44, 440);
                    delay += 55;
                }
            }
        }
    }

    private bool HistoryRowContainersArePending()
    {
        foreach (var group in HistoryList.Items)
        {
            if (HistoryList.ContainerFromItem(group) is not DependencyObject groupContainer)
            {
                return true;
            }

            var (_, rows) = FindHistoryGroupParts(groupContainer);
            if (rows is null || PageEntranceAnimationScheduler.RequiresLayoutPass(rows.Items.Count, RealizedCountOf(rows)))
            {
                return true;
            }
        }

        return false;
    }

    private static int RealizedCountOf(ItemsControl list) =>
        list.Items.Cast<object>().Count(item => list.ContainerFromItem(item) is UIElement);

    /// <summary>
    /// Pulls the day label row and the entry list out of one rendered day group. The group's
    /// template root is a StackPanel holding the header grid followed by the entries.
    /// </summary>
    private static (UIElement? Header, ItemsControl? Rows) FindHistoryGroupParts(DependencyObject groupContainer)
    {
        var rows = FindDescendant<ItemsControl>(groupContainer);
        if (rows is null)
        {
            return (null, null);
        }

        var header = VisualTreeHelper.GetParent(rows) is StackPanel panel && panel.Children.Count > 0
            ? panel.Children[0] as UIElement
            : null;

        return (ReferenceEquals(header, rows) ? null : header, rows);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                var child = VisualTreeHelper.GetChild(current, index);
                if (child is T match)
                {
                    return match;
                }

                queue.Enqueue(child);
            }
        }

        return null;
    }

    private Task WaitForNextLayoutAsync()
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void LayoutUpdated(object? sender, object args)
        {
            RootGrid.LayoutUpdated -= LayoutUpdated;
            completion.TrySetResult(null);
        }

        RootGrid.LayoutUpdated += LayoutUpdated;
        return completion.Task;
    }

    private static int GetItemCount(IReadOnlyList<ItemsControl> lists) => lists.Sum(list => list.Items.Count);

    private static int GetRealizedContainerCount(IReadOnlyList<ItemsControl> lists) => lists.Sum(list =>
        list.Items.Cast<object>().Count(item => list.ContainerFromItem(item) is UIElement));

    private void SetItemContainersImmediatelyVisible(string? tag)
    {
        if (tag == "history")
        {
            foreach (var group in HistoryList.Items)
            {
                if (HistoryList.ContainerFromItem(group) is not DependencyObject groupContainer)
                {
                    continue;
                }

                if (groupContainer is UIElement groupElement)
                {
                    SetImmediatelyVisible(groupElement);
                }

                var (header, rows) = FindHistoryGroupParts(groupContainer);
                if (header is not null)
                {
                    SetImmediatelyVisible(header);
                }

                if (rows is null)
                {
                    continue;
                }

                foreach (var item in rows.Items)
                {
                    if (rows.ContainerFromItem(item) is UIElement row)
                    {
                        SetImmediatelyVisible(row);
                    }
                }
            }

            return;
        }

        foreach (var list in GetAnimatedItemLists(tag))
        {
            foreach (var item in list.Items)
            {
                if (list.ContainerFromItem(item) is UIElement container)
                {
                    SetImmediatelyVisible(container);
                }
            }
        }
    }

    private IReadOnlyList<ItemsControl> GetAnimatedItemLists(string? tag) => tag switch
    {
        "queue" => [InProgressQueueList, CompletedQueueList, FailedQueueList],
        "history" => [HistoryList],
        _ => [],
    };

    private void AnimatePageTitle(UIElement element, double delayMilliseconds)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 0;
        visual.Properties.InsertVector3("Translation", new Vector3(0, 24, 0));

        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1));
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 1);
        opacity.DelayTime = MotionTime(delayMilliseconds);
        opacity.Duration = MotionTime(220);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.DelayTime = MotionTime(delayMilliseconds);
        translation.Duration = MotionTime(400);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    private void AnimateOpacity(UIElement element, double delayMilliseconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 0;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, 1);
        animation.DelayTime = MotionTime(delayMilliseconds);
        animation.Duration = MotionTime(160);
        visual.StartAnimation("Opacity", animation);
    }

    private void AnimateSlideUp(UIElement element, double delayMilliseconds)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 0;
        visual.Properties.InsertVector3("Translation", new Vector3(0, 24, 0));

        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.DelayTime = MotionTime(delayMilliseconds);
        opacity.Duration = MotionTime(220);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.DelayTime = MotionTime(delayMilliseconds);
        translation.Duration = MotionTime(220);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    private void AnimateSlideFromRight(UIElement element, double delayMilliseconds, float offset, double durationMilliseconds)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 0;
        visual.Properties.InsertVector3("Translation", new Vector3(offset, 0, 0));

        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1));
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 1);
        opacity.DelayTime = MotionTime(delayMilliseconds);
        opacity.Duration = MotionTime(180);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.DelayTime = MotionTime(delayMilliseconds);
        translation.Duration = MotionTime(durationMilliseconds);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    private TimeSpan MotionTime(double milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds * Math.Clamp(_settings.MotionTempo, 0.4, 2));

    private static void SetImmediatelyVisible(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void StartAmbientAnimation()
    { StopAmbientAnimation(AuroraOne); StopAmbientAnimation(AuroraTwo); StopAmbientAnimation(AuroraThree); StopAmbientAnimation(InProgressStatusDot); StopAmbientAnimation(UrlGlow);
        if (!_uiSettings.AnimationsEnabled)
        {
            UrlGlow.Opacity = 0.5;
            return;
        }

        StartAmbientDrift(AuroraOne, new Vector3(46, 30, 0), MotionTime(16000));
        StartAmbientDrift(AuroraTwo, new Vector3(-38, -24, 0), MotionTime(19000));
        StartAmbientDrift(AuroraThree, new Vector3(-30, 26, 0), MotionTime(22000));
        StartStatusPulse(InProgressStatusDot);
        StartGlowPulse(UrlGlow);
    }

    private static void StartAmbientDrift(UIElement element, Vector3 destination, TimeSpan duration)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.Opacity = 0.82f;
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.Zero);
        animation.InsertKeyFrame(0.5f, destination);
        animation.InsertKeyFrame(1, Vector3.Zero);
        animation.Duration = duration;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation", animation);
    }

    private void StartStatusPulse(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = 1;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0.35f);
        animation.InsertKeyFrame(0.5f, 1);
        animation.InsertKeyFrame(1, 0.35f);
        animation.Duration = MotionTime(1050);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
    }

    private void StartGlowPulse(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
        animation.InsertKeyFrame(0, 0.5f, easing);
        animation.InsertKeyFrame(0.5f, 0.9f, easing);
        animation.InsertKeyFrame(1, 0.5f, easing);
        animation.Duration = MotionTime(3200);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
    }

    private void ProgressShimmerLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element)
        {
            StartProgressShimmer(element);
        }
    }

    private void ProgressTrackSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is Border { Child: Grid grid })
        {
            foreach (var shimmer in grid.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>())
            {
                StartProgressShimmer(shimmer);
            }
        }
    }

    private void StartProgressShimmer(FrameworkElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        var trackWidth = (float)((element.Parent as FrameworkElement)?.ActualWidth ?? 0);
        if (trackWidth <= 0 && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) is FrameworkElement ancestor)
        {
            trackWidth = (float)ancestor.ActualWidth;
        }

        if (!_uiSettings.AnimationsEnabled || trackWidth <= 0)
        {
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            element.Opacity = 0;
            return;
        }

        element.Opacity = 1;
        var travel = trackWidth + (float)element.Width;
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
        animation.InsertKeyFrame(0, new Vector3(-(float)element.Width, 0, 0));
        animation.InsertKeyFrame(1, new Vector3(travel, 0, 0), easing);
        animation.Duration = MotionTime(2100);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation", animation);
    }

    private static void StopAmbientAnimation(UIElement element)
    {
        // Translation has to be enabled before it can be stopped; Composition rejects the
        // property name outright on an element that has never opted in.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void RootGridSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compactSidebar = args.NewSize.Width < 900;
        SidebarColumn.Width = new GridLength(compactSidebar ? 56 : 196);
        SidebarWordmark.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        StorageCard.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        NavDownloadLabel.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        NavQueueLabel.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        NavHistoryLabel.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        NavSettingsLabel.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        QueueCountBadge.Visibility = compactSidebar || QueueCountBadgeText.Text == "0"
            ? Visibility.Collapsed
            : Visibility.Visible;

        var narrow = args.NewSize.Width < 780;
        OptionsPanel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        OptionsPanel.Spacing = narrow ? 14 : 26;
        CookieBox.Width = narrow ? double.NaN : 168;

        var stackedQueue = args.NewSize.Width < 1050;
        QueueInProgressColumn.Width = new GridLength(1, GridUnitType.Star);
        QueueCompletedColumn.Width = stackedQueue ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        QueueFailedColumn.Width = stackedQueue ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        QueuePrimaryRow.Height = new GridLength(1, GridUnitType.Star);
        QueueSecondaryRow.Height = stackedQueue ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        QueueTertiaryRow.Height = stackedQueue ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(InProgressQueueSection, 0);
        Grid.SetColumn(CompletedQueueSection, stackedQueue ? 0 : 1);
        Grid.SetColumn(FailedQueueSection, stackedQueue ? 0 : 2);
        Grid.SetRow(InProgressQueueSection, 0);
        Grid.SetRow(CompletedQueueSection, stackedQueue ? 1 : 0);
        Grid.SetRow(FailedQueueSection, stackedQueue ? 2 : 0);
        Grid.SetColumnSpan(QueueEmptyState, stackedQueue ? 1 : 3);
        Grid.SetRowSpan(QueueEmptyState, stackedQueue ? 3 : 1);
    }

    private void ShowError(string title, string message) => ShowStatus(title, UserFacingErrorMapper.Map(message), InfoBarSeverity.Error, false);

    private void ShowStatus(string title, string message, InfoBarSeverity severity, bool showRetry)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
        RetryToolsButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatDuration(TimeSpan? duration) => duration is TimeSpan value ? value.ToString(@"h\:mm\:ss") : "時長未提供";
}
