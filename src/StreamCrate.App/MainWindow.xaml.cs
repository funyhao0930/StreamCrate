using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Diagnostics;
using StreamCrate.Infrastructure.Processes;
using StreamCrate.Infrastructure.Queue;
using StreamCrate.Infrastructure.Storage;
using StreamCrate.Infrastructure.Tooling;

namespace StreamCrate.App;

public sealed partial class MainWindow : Window
{
    private readonly ToolchainManager _toolchain = new(new HttpClient());
    private readonly DownloadQueueService _queue;
    private readonly IHistoryStore _history;
    private readonly IAppSettingsStore _settingsStore;
    private AppSettings _settings = AppSettings.CreateDefault();
    private readonly Dictionary<Guid, DownloadRequest> _sessionRequests = [];
    private bool _toolsReady;
    private MediaItem? _probedMedia;
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
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        ApplySettings();
        try { await _toolchain.EnsureAvailableAsync(); _toolsReady = true; StatusBar.Title = "工具已準備"; StatusBar.Message = "yt-dlp 與 FFmpeg 已驗證，可開始解析網址。"; StatusBar.Severity = InfoBarSeverity.Success; }
        catch (Exception exception) { StatusBar.Title = "工具準備失敗"; StatusBar.Message = exception.Message; StatusBar.Severity = InfoBarSeverity.Error; }
    }

    private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        DownloadPanel.Visibility = item.Tag?.ToString() == "download" ? Visibility.Visible : Visibility.Collapsed;
        QueuePanel.Visibility = item.Tag?.ToString() == "queue" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = item.Tag?.ToString() == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = item.Tag?.ToString() == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (item.Tag?.ToString() == "queue") RenderQueue();
        if (item.Tag?.ToString() == "history") _ = RenderHistoryAsync();
    }

    private async void ProbeClicked(object sender, RoutedEventArgs args)
    {
        if (!Uri.TryCreate(UrlBox.Text?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { ShowError("無效網址", "請輸入完整的 http 或 https 網址。"); return; }
        if (!_toolsReady) { await InitializeAsync(); if (!_toolsReady) return; }
        var cookies = await GetCookieSelectionAsync();
        if (cookies is null) return;
        try { var result = await new YtDlpMediaProbeService(_toolchain.YtDlpPath).ProbeAsync(uri, cookies); ResultText.Text = result.IsPlaylist ? $"已找到播放清單：{result.Playlist!.Title}（{result.Playlist.Items.Count} 部）" : $"已找到影片：{result.Media!.Title}"; _probedMedia = result.Media ?? result.Playlist?.Items.FirstOrDefault(); _probedCookies = cookies; QueueButton.IsEnabled = _probedMedia is not null; }
        catch (Exception exception) { ShowError("解析失敗", UserFacingErrorMapper.Map(exception.Message)); }
    }

    private async void QueueClicked(object sender, RoutedEventArgs args)
    {
        if (_probedMedia is null) return;
        var request = new DownloadRequest(_probedMedia, _settings.DownloadDirectory, FormatBox.SelectedIndex == 1 ? DownloadFormat.Mp3 : DownloadFormat.Mp4, (VideoQuality)QualityBox.SelectedIndex, _probedCookies);
        var job = await _queue.EnqueueAsync(request); _sessionRequests[job.Id] = request; StatusBar.Title = "已加入佇列"; StatusBar.Message = "可在下載佇列查看進度或取消工作。"; RenderQueue();
    }

    private async Task<CookieSelection?> GetCookieSelectionAsync()
    {
        var source = (CookieBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (source != "file")
        {
            return source switch { "chrome" => new CookieSelection(CookieSource.Chrome), "edge" => new CookieSelection(CookieSource.Edge), "firefox" => new CookieSelection(CookieSource.Firefox), _ => CookieSelection.None };
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".txt");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            StatusBar.Title = "未選取 cookies.txt";
            StatusBar.Message = "請選取僅供本次使用的 Netscape cookies.txt 檔案。";
            StatusBar.Severity = InfoBarSeverity.Warning;
            return null;
        }

        return new CookieSelection(CookieSource.CookiesFile, file.Path);
    }
    private async Task ExecuteDownloadAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var specification = new YtDlpCommandFactory().CreateDownload(job.Request, Path.GetDirectoryName(_toolchain.FfmpegPath)!);
        await new YtDlpProcessRunner().RunAsync(specification with { ExecutablePath = _toolchain.YtDlpPath }, progress, cancellationToken);
    }
    private void QueueJobChanged(object? sender, DownloadJob job) => DispatcherQueue.TryEnqueue(async () => { if (job.State == DownloadJobState.Failed) ShowError("下載失敗", job.ErrorMessage ?? "下載工具未提供詳細錯誤。"); if (job.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled) await _history.SaveAsync(new(job.Id, job.Request.Media.Extractor, job.Request.Media.MediaId, job.Request.Media.Title, job.Request.Media.SourceUrl, job.Request.Format, job.Request.Quality, job.Request.OutputDirectory, job.State, job.ErrorCategory, job.CreatedAt)); RenderQueue(); });
    private void RenderQueue()
    {
        QueueList.Children.Clear(); var jobs = _queue.GetSnapshot();
        if (jobs.Count == 0) { QueueList.Children.Add(new TextBlock { Text = "目前沒有下載工作。", Opacity = 0.7 }); return; }
        foreach (var job in jobs) { var panel = new StackPanel { Spacing = 4, Padding = new Thickness(16), Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DeepSeaCardBrush"] }; panel.Children.Add(new TextBlock { Text = job.Request.Media.Title, FontSize = 17 }); panel.Children.Add(new TextBlock { Text = $"{StateText(job.State)}  ·  {job.Request.Format}  ·  {job.Request.Quality}", Opacity = 0.75 }); if (job.State == DownloadJobState.Failed && job.ErrorMessage is not null) panel.Children.Add(new TextBlock { Text = UserFacingErrorMapper.Map(job.ErrorMessage), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 }); if (job.Progress?.Percent is double percent) panel.Children.Add(new ProgressBar { Value = percent, Maximum = 100, Height = 7 }); if (job.State is DownloadJobState.Queued or DownloadJobState.Downloading) { var button = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => _queue.Cancel(job.Id); panel.Children.Add(button); } QueueList.Children.Add(panel); }
    }
    private async Task RenderHistoryAsync()
    {
        HistoryList.Children.Clear(); var entries = await _history.SearchAsync(HistorySearchBox.Text, null);
        if (entries.Count == 0) { HistoryList.Children.Add(new TextBlock { Text = "尚無歷史紀錄。完成、失敗或取消的下載會出現在這裡。", Opacity = 0.7 }); return; }
        foreach (var entry in entries) { var panel = new StackPanel { Spacing = 4, Padding = new Thickness(16), Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DeepSeaCardBrush"] }; panel.Children.Add(new TextBlock { Text = entry.Title, FontSize = 17 }); panel.Children.Add(new TextBlock { Text = $"{StateText(entry.State)} · {entry.Format} · {entry.CreatedAt.LocalDateTime:g}", Opacity = 0.75 }); var open = new Button { Content = "開啟下載資料夾", HorizontalAlignment = HorizontalAlignment.Left }; open.Click += (_, _) => Windows.System.Launcher.LaunchFolderPathAsync(entry.OutputPath).AsTask(); panel.Children.Add(open); HistoryList.Children.Add(panel); }
    }
    private void HistorySearchChanged(object sender, TextChangedEventArgs args) => _ = RenderHistoryAsync();
    private async void ClearHistoryClicked(object sender, RoutedEventArgs args) { await _history.ClearAsync(); await RenderHistoryAsync(); }
    private async void SaveSettingsClicked(object sender, RoutedEventArgs args)
    {
        var directory = DownloadFolderBox.Text.Trim(); if (!Directory.Exists(directory)) { SettingsMessage.Text = "請輸入已存在的下載資料夾。"; return; }
        _settings = new(directory, DefaultFormatBox.SelectedIndex == 1 ? DownloadFormat.Mp3 : DownloadFormat.Mp4, (VideoQuality)DefaultQualityBox.SelectedIndex, ThemeBox.SelectedIndex == 1 ? AppTheme.Light : AppTheme.Dark); await _settingsStore.SaveAsync(_settings); ApplySettings(); SettingsMessage.Text = "設定已保存，之後的下載會使用新預設。";
    }
    private void ApplySettings() { DownloadFolderBox.Text = _settings.DownloadDirectory; DefaultFormatBox.SelectedIndex = _settings.DefaultFormat == DownloadFormat.Mp3 ? 1 : 0; DefaultQualityBox.SelectedIndex = (int)_settings.DefaultQuality; ThemeBox.SelectedIndex = _settings.Theme == AppTheme.Light ? 1 : 0; FormatBox.SelectedIndex = DefaultFormatBox.SelectedIndex; QualityBox.SelectedIndex = DefaultQualityBox.SelectedIndex; RootGrid.RequestedTheme = _settings.Theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark; }
    private void ShowError(string title, string message) { StatusBar.Title = title; StatusBar.Message = UserFacingErrorMapper.Map(message); StatusBar.Severity = InfoBarSeverity.Error; }
    private static string StateText(DownloadJobState state) => state switch { DownloadJobState.Queued => "等待中", DownloadJobState.Downloading => "下載中", DownloadJobState.Completed => "已完成", DownloadJobState.Cancelled => "已取消", DownloadJobState.Failed => "下載失敗", _ => state.ToString() };
}
