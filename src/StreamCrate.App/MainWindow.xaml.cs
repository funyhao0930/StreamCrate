using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Processes;
using StreamCrate.Infrastructure.Tooling;
using StreamCrate.Infrastructure.Queue;

namespace StreamCrate.App;

public sealed partial class MainWindow : Window
{
    private readonly ToolchainManager _toolchain = new(new HttpClient());
    private readonly DownloadQueueService _queue;
    private bool _toolsReady;
    private MediaItem? _probedMedia;
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1050, 720));
        _queue = new DownloadQueueService(ExecuteDownloadAsync);
        _queue.JobChanged += QueueJobChanged;
    }

    private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        PageTitle.Text = item.Content?.ToString() ?? "StreamCrate";
        ResultText.Text = item.Tag?.ToString() switch
        {
            "queue" => "下載佇列會以 FIFO 方式一次執行一項工作。",
            "history" => "完成與失敗的下載會保存在本機歷史紀錄。",
            "settings" => "可設定下載資料夾、語言、主題與工具更新。",
            _ => string.Empty,
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _toolchain.EnsureAvailableAsync();
            _toolsReady = true;
            StatusBar.Title = "工具已準備";
            StatusBar.Message = "yt-dlp 與 FFmpeg 已驗證，可開始解析網址。";
            StatusBar.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception)
        {
            StatusBar.Title = "工具準備失敗";
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
        }
    }

    private async void ProbeClicked(object sender, RoutedEventArgs args)
    {
        if (!Uri.TryCreate(UrlBox.Text?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "無效網址";
            StatusBar.Message = "請輸入完整的 http 或 https 網址。";
            return;
        }

        if (!_toolsReady)
        {
            await InitializeAsync();
            if (!_toolsReady)
            {
                return;
            }
        }

        try
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = "正在解析";
            StatusBar.Message = "正在讀取媒體資訊，尚未開始下載。";
            var result = await new YtDlpMediaProbeService(_toolchain.YtDlpPath).ProbeAsync(uri, GetCookieSelection());
            ResultText.Text = result.IsPlaylist
                ? $"已找到播放清單：{result.Playlist!.Title}（{result.Playlist.Items.Count} 部）"
                : $"已找到影片：{result.Media!.Title}";
            _probedMedia = result.Media ?? result.Playlist?.Items.FirstOrDefault();
            QueueButton.IsEnabled = _probedMedia is not null;
            StatusBar.Title = "解析完成";
            StatusBar.Message = "請選擇格式與畫質後加入佇列。";
            StatusBar.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception)
        {
            StatusBar.Title = "解析失敗";
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
        }
    }

    private CookieSelection GetCookieSelection() => ((CookieBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()) switch
    {
        "chrome" => new CookieSelection(CookieSource.Chrome),
        "edge" => new CookieSelection(CookieSource.Edge),
        "firefox" => new CookieSelection(CookieSource.Firefox),
        _ => CookieSelection.None,
    };

    private async void QueueClicked(object sender, RoutedEventArgs args)
    {
        if (_probedMedia is null)
        {
            return;
        }

        var format = FormatBox.SelectedIndex == 1 ? DownloadFormat.Mp3 : DownloadFormat.Mp4;
        var quality = QualityBox.SelectedIndex switch
        {
            1 => VideoQuality.P2160,
            2 => VideoQuality.P1440,
            3 => VideoQuality.P1080,
            4 => VideoQuality.P720,
            _ => VideoQuality.Best,
        };
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        await _queue.EnqueueAsync(new DownloadRequest(_probedMedia, directory, format, quality, GetCookieSelection()));
        StatusBar.Title = "已加入佇列";
        StatusBar.Message = "StreamCrate 一次只下載一項工作。";
        StatusBar.Severity = InfoBarSeverity.Informational;
    }

    private async Task ExecuteDownloadAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var specification = new YtDlpCommandFactory().CreateDownload(job.Request, Path.GetDirectoryName(_toolchain.FfmpegPath)!);
        var progress = new Progress<DownloadProgress>(value =>
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusBar.Title = $"下載中 {value.Percent:0.0}%";
                StatusBar.Message = $"{value.Speed ?? ""} · 剩餘 {value.Eta}";
                StatusBar.Severity = InfoBarSeverity.Informational;
            }));
        await new YtDlpProcessRunner().RunAsync(specification with { ExecutablePath = _toolchain.YtDlpPath }, progress, cancellationToken);
    }

    private void QueueJobChanged(object? sender, DownloadJob job) => DispatcherQueue.TryEnqueue(() =>
    {
        if (job.State == DownloadJobState.Completed)
        {
            StatusBar.Title = "下載完成";
            StatusBar.Message = job.Request.Media.Title;
            StatusBar.Severity = InfoBarSeverity.Success;
        }
        else if (job.State is DownloadJobState.Failed or DownloadJobState.Cancelled)
        {
            StatusBar.Title = job.State == DownloadJobState.Failed ? "下載失敗" : "下載已取消";
            StatusBar.Message = job.Request.Media.Title;
            StatusBar.Severity = InfoBarSeverity.Error;
        }
    });
}
