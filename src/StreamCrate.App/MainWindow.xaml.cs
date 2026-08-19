using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamCrate.App.Presentation;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Diagnostics;
using StreamCrate.Infrastructure.Processes;
using StreamCrate.Infrastructure.Queue;
using StreamCrate.Infrastructure.Storage;
using StreamCrate.Infrastructure.Tooling;
using Windows.Storage.Pickers;

namespace StreamCrate.App;

public sealed partial class MainWindow : Window
{
    private readonly ToolchainManager _toolchain = new(new HttpClient());
    private readonly DownloadQueueService _queue;
    private readonly IHistoryStore _history;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ObservableCollection<QueueItem> _queueItems = [];
    private readonly ObservableCollection<HistoryItem> _historyItems = [];
    private readonly HashSet<Guid> _storedHistoryIds = [];
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _toolsReady;
    private bool _isInitializing;
    private bool _isProbing;
    private MediaItem? _probedMedia;
    private PlaylistSelection? _playlistSelection;
    private CookieSelection _probedCookies = CookieSelection.None;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamCrate");
        _settingsStore = new JsonAppSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        _history = new SqliteHistoryStore(Path.Combine(dataDirectory, "history.db"));
        _queue = new DownloadQueueService(ExecuteDownloadAsync);
        _queue.JobChanged += QueueJobChanged;
        QueueList.ItemsSource = _queueItems;
        HistoryList.ItemsSource = _historyItems;
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

    private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        ShowPanel(item.Tag?.ToString());
    }

    private void ShowPanel(string? tag)
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
            _ = RenderHistoryAsync();
        }
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
        UpdatePlaylistSelectionSummary();
        ResolvedResultPanel.Visibility = Visibility.Visible;
    }

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
        var format = FormatBox.SelectedIndex == 1 ? DownloadFormat.Mp3 : DownloadFormat.Mp4;
        var quality = (VideoQuality)Math.Max(QualityBox.SelectedIndex, 0);
        if (_playlistSelection is not null)
        {
            return _playlistSelection.CreateRequests(_settings.DownloadDirectory, format, quality, _probedCookies);
        }

        return _probedMedia is null
            ? []
            : [new DownloadRequest(_probedMedia, _settings.DownloadDirectory, format, quality, _probedCookies)];
    }

    private void ViewQueueClicked(object sender, RoutedEventArgs args)
    {
        RootNavigation.SelectedItem = QueueNavigationItem;
        ShowPanel("queue");
    }

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

    private async Task ExecuteDownloadAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var specification = new YtDlpCommandFactory().CreateDownload(job.Request, Path.GetDirectoryName(_toolchain.FfmpegPath)!);
        await new YtDlpProcessRunner().RunAsync(specification with { ExecutablePath = _toolchain.YtDlpPath }, progress, cancellationToken);
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
                    job.CreatedAt));
            }

            RenderQueue();
        }
        catch (Exception exception)
        {
            ShowError("無法保存歷史紀錄", exception.Message);
        }
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
        _queueItems.Clear();
        foreach (var job in _queue.GetSnapshot())
        {
            _queueItems.Add(new QueueItem(job));
        }

        QueueEmptyState.Visibility = _queueItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RenderHistoryAsync()
    {
        try
        {
            var entries = await _history.SearchAsync(HistorySearchBox.Text, null);
            _historyItems.Clear();
            foreach (var entry in entries)
            {
                _historyItems.Add(new HistoryItem(entry));
            }

            HistoryEmptyState.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ShowError("無法讀取歷史紀錄", exception.Message);
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

    private async void OpenHistoryFolderClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string outputPath)
        {
            await Windows.System.Launcher.LaunchFolderPathAsync(outputPath);
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
            DefaultFormatBox.SelectedIndex == 1 ? DownloadFormat.Mp3 : DownloadFormat.Mp4,
            (VideoQuality)Math.Max(DefaultQualityBox.SelectedIndex, 0),
            ThemeBox.SelectedIndex == 1 ? AppTheme.Light : AppTheme.Dark);
        await _settingsStore.SaveAsync(_settings);
        ApplySettings();
        SettingsMessage.Text = "設定已保存，之後的下載會使用新預設。";
    }

    private void ApplySettings()
    {
        DownloadFolderBox.Text = _settings.DownloadDirectory;
        DefaultFormatBox.SelectedIndex = _settings.DefaultFormat == DownloadFormat.Mp3 ? 1 : 0;
        DefaultQualityBox.SelectedIndex = (int)_settings.DefaultQuality;
        ThemeBox.SelectedIndex = _settings.Theme == AppTheme.Light ? 1 : 0;
        FormatBox.SelectedIndex = DefaultFormatBox.SelectedIndex;
        QualityBox.SelectedIndex = DefaultQualityBox.SelectedIndex;
        RootGrid.RequestedTheme = _settings.Theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
    }

    private void RootGridSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 780;
        OptionsPanel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        SettingsOptionsPanel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        CookieBox.Width = narrow ? double.NaN : 230;
        FormatBox.Width = narrow ? double.NaN : 160;
        QualityBox.Width = narrow ? double.NaN : 160;
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
