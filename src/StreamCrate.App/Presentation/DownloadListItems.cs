using Microsoft.UI.Xaml;
using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Diagnostics;

namespace StreamCrate.App.Presentation;

internal sealed class QueueItem
{
    public QueueItem(DownloadJob job)
    {
        Id = job.Id;
        Title = job.Request.Media.Title;
        State = DownloadStateText.Get(job.State);
        Details = $"{State} · {FormatText(job.Request.Format)} · {QualityText(job.Request.Quality)}";
        ProgressPercent = job.Progress?.Percent ?? 0;
        ProgressDetails = BuildProgressDetails(job.Progress);
        ProgressVisibility = job.Progress?.Percent is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressDetailsVisibility = string.IsNullOrWhiteSpace(ProgressDetails) ? Visibility.Collapsed : Visibility.Visible;
        ErrorMessage = job.ErrorMessage is null ? string.Empty : UserFacingErrorMapper.Map(job.ErrorMessage);
        ErrorVisibility = string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
        CancelVisibility = job.State is DownloadJobState.Queued or DownloadJobState.Downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    public Guid Id { get; }
    public string Title { get; }
    public string State { get; }
    public string Details { get; }
    public double ProgressPercent { get; }
    public string ProgressDetails { get; }
    public Visibility ProgressVisibility { get; }
    public Visibility ProgressDetailsVisibility { get; }
    public string ErrorMessage { get; }
    public Visibility ErrorVisibility { get; }
    public Visibility CancelVisibility { get; }

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

internal sealed class HistoryItem
{
    public HistoryItem(HistoryEntry entry)
    {
        Title = entry.Title;
        Details = $"{DownloadStateText.Get(entry.State)} · {entry.Format} · {entry.CreatedAt.LocalDateTime:g}";
        OutputPath = entry.OutputPath;
    }

    public string Title { get; }
    public string Details { get; }
    public string OutputPath { get; }
}
