using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Diagnostics;

namespace StreamCrate.App.Presentation;

internal sealed class QueueItem : ObservableObject
{
    public QueueItem(DownloadJob job)
    {
        Id = job.Id;
        Title = job.Request.Media.Title;
        OutputPath = job.Request.OutputDirectory;
        Request = job.Request;
        Thumbnail = Thumbnails.Load(job.Request.Media.ThumbnailUrl, QueueThumbnailWidth);
        ThumbnailVisibility = Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        Update(job);
    }

    private const int QueueThumbnailWidth = 44;

    public Guid Id { get; }
    public string Title { get; }
    public string OutputPath { get; }
    public DownloadRequest Request { get; }

    /// <summary>Still for the row, or null when the source published none.</summary>
    public ImageSource? Thumbnail { get; }

    /// <summary>Keeps the empty placeholder tile visible when there is no still to show.</summary>
    public Visibility ThumbnailVisibility { get; }

    private string _section = string.Empty;
    public string Section
    {
        get => _section;
        private set => SetProperty(ref _section, value);
    }

    private string _state = string.Empty;
    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    private string _details = string.Empty;
    public string Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    private string _progressPercentText = string.Empty;

    /// <summary>
    /// Readout beside the bar: the design's "24%" once yt-dlp reports a percent, and the state
    /// word ("等待中" / "處理中") before then, the way the mockup's converting card reads "轉檔".
    /// </summary>
    public string ProgressPercentText
    {
        get => _progressPercentText;
        private set => SetProperty(ref _progressPercentText, value);
    }

    private string _progressDetails = string.Empty;
    public string ProgressDetails
    {
        get => _progressDetails;
        private set => SetProperty(ref _progressDetails, value);
    }

    private Visibility _progressVisibility;
    public Visibility ProgressVisibility
    {
        get => _progressVisibility;
        private set => SetProperty(ref _progressVisibility, value);
    }

    private Visibility _progressDetailsVisibility;
    public Visibility ProgressDetailsVisibility
    {
        get => _progressDetailsVisibility;
        private set => SetProperty(ref _progressDetailsVisibility, value);
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private Visibility _errorVisibility;

    /// <summary>
    /// Hides the error box and the copy action for a stopped job that carries no message, which is
    /// the case for a user-cancelled download.
    /// </summary>
    public Visibility ErrorVisibility
    {
        get => _errorVisibility;
        private set => SetProperty(ref _errorVisibility, value);
    }

    private Visibility _completedBadgeVisibility;
    public Visibility CompletedBadgeVisibility
    {
        get => _completedBadgeVisibility;
        private set => SetProperty(ref _completedBadgeVisibility, value);
    }

    private Visibility _skippedBadgeVisibility;
    public Visibility SkippedBadgeVisibility
    {
        get => _skippedBadgeVisibility;
        private set => SetProperty(ref _skippedBadgeVisibility, value);
    }

    private Visibility _cancelVisibility;
    public Visibility CancelVisibility
    {
        get => _cancelVisibility;
        private set => SetProperty(ref _cancelVisibility, value);
    }

    public void Update(DownloadJob job)
    {
        Section = GetSection(job.State);
        State = DownloadStateText.Get(job.State);
        Details = $"{State} · {FormatText(job.Request.Format)} · {QualityText(job.Request.Quality)}";
        var percent = job.Progress?.Percent;
        ProgressPercent = percent ?? 0;
        ProgressPercentText = percent is double value ? $"{Math.Round(value):0}%" : State;
        ProgressDetails = BuildProgressDetails(job.Progress);
        // The design keeps a track under every in-progress row, so the card must not lose its bar
        // in the window between the job starting and yt-dlp emitting its first percent.
        ProgressVisibility = GetSection(job.State) == "InProgress" ? Visibility.Visible : Visibility.Collapsed;
        ProgressDetailsVisibility = string.IsNullOrWhiteSpace(ProgressDetails) ? Visibility.Collapsed : Visibility.Visible;
        ErrorMessage = job.ErrorMessage is null ? string.Empty : UserFacingErrorMapper.Map(job.ErrorMessage);
        ErrorVisibility = string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
        var skipped = job.State is DownloadJobState.SkippedExisting;
        SkippedBadgeVisibility = skipped ? Visibility.Visible : Visibility.Collapsed;
        CompletedBadgeVisibility = skipped ? Visibility.Collapsed : Visibility.Visible;
        CancelVisibility = job.State is DownloadJobState.Queued or DownloadJobState.Downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Which board column a job belongs to. A job the user cancelled has stopped for good, so it
    /// files under 下載失敗 next to the retry action rather than lingering in 下載中; a job skipped
    /// because the file already exists counts as a finished download.
    /// </summary>
    public static string GetSection(DownloadJobState state) => state switch
    {
        DownloadJobState.Completed or DownloadJobState.SkippedExisting => "Completed",
        DownloadJobState.Failed or DownloadJobState.Cancelled => "Failed",
        _ => "InProgress",
    };

    private static string BuildProgressDetails(DownloadProgress? progress)
    {
        if (progress is null)
        {
            return string.Empty;
        }

        return string.Join(" · ", new[]
        {
            progress.Speed,
            progress.TotalSize,
            progress.Eta is TimeSpan eta ? $"剩餘 {eta:mm\\:ss}" : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatText(DownloadFormat format) => format == DownloadFormat.Mp3 ? "MP3" : "MP4";

    private static string QualityText(VideoQuality quality) => quality switch
    {
        VideoQuality.Best => "最佳",
        VideoQuality.P2160 => "2160p",
        VideoQuality.P1440 => "1440p",
        VideoQuality.P1080 => "1080p",
        VideoQuality.P720 => "720p",
        _ => quality.ToString(),
    };
}

internal static class QueueSectionSynchronizer
{
    public static void Synchronize(ObservableCollection<QueueItem> items, IEnumerable<QueueItem> expectedItems)
    {
        var expected = expectedItems.ToArray();
        foreach (var staleItem in items.Where(item => !expected.Contains(item)).ToArray())
        {
            items.Remove(staleItem);
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var item = expected[index];
            if (index < items.Count && items[index] == item)
            {
                continue;
            }

            var existingIndex = items.IndexOf(item);
            if (existingIndex >= 0)
            {
                items.Move(existingIndex, index);
            }
            else
            {
                items.Insert(index, item);
            }
        }
    }
}

internal sealed class HistoryItem
{
    public HistoryItem(HistoryEntry entry)
    {
        Title = entry.Title;
        Details = $"{DownloadStateText.Get(entry.State)} · {FormatText(entry.Format)} · {entry.CreatedAt.LocalDateTime:t}";
        OutputPath = entry.OutputPath;
        SourceUrl = entry.SourceUrl.ToString();
        IsFailed = entry.State is DownloadJobState.Failed;
        SizeText = DescribeSize(entry.OutputPath);
        Thumbnail = Thumbnails.Load(entry.ThumbnailUrl, HistoryThumbnailWidth);
        ThumbnailVisibility = Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public string Title { get; }
    public string Details { get; }
    public string OutputPath { get; }
    public string SourceUrl { get; }
    public bool IsFailed { get; }
    public string SizeText { get; }
    public ImageSource? Thumbnail { get; }
    public Visibility ThumbnailVisibility { get; }

    private const int HistoryThumbnailWidth = 52;

    /// <summary>
    /// Bytes on disk for the row, or 0 when the file is gone; also feeds the "佔用空間" metric.
    /// </summary>
    public static long SizeOnDisk(string outputPath)
    {
        try
        {
            return File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string DescribeSize(string outputPath)
    {
        var bytes = SizeOnDisk(outputPath);
        if (bytes <= 0)
        {
            return string.Empty;
        }

        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024
            ? $"{megabytes / 1024:0.#} GB"
            : $"{megabytes:0} MB";
    }

    private static string FormatText(DownloadFormat format) => format == DownloadFormat.Mp3 ? "MP3" : "MP4";
}
