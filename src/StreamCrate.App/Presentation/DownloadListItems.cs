using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
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
        Update(job);
    }

    public Guid Id { get; }
    public string Title { get; }
    public string OutputPath { get; }
    public DownloadRequest Request { get; }

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
        private set
        {
            if (SetProperty(ref _progressPercent, value))
            {
                OnPropertyChanged(nameof(ProgressPercentText));
            }
        }
    }

    /// <summary>Percent shown beside the bar, matching the design's "24%" readout.</summary>
    public string ProgressPercentText => $"{Math.Round(_progressPercent):0}%";

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
        ProgressPercent = job.Progress?.Percent ?? 0;
        ProgressDetails = BuildProgressDetails(job.Progress);
        ProgressVisibility = job.Progress?.Percent is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressDetailsVisibility = string.IsNullOrWhiteSpace(ProgressDetails) ? Visibility.Collapsed : Visibility.Visible;
        ErrorMessage = job.ErrorMessage is null ? string.Empty : UserFacingErrorMapper.Map(job.ErrorMessage);
        CancelVisibility = job.State is DownloadJobState.Queued or DownloadJobState.Downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string GetSection(DownloadJobState state) => state switch
    {
        DownloadJobState.Completed => "Completed",
        DownloadJobState.Failed => "Failed",
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
    public HistoryItem(HistoryEntry entry, double indent = 0)
    {
        Title = entry.Title;
        Details = $"{DownloadStateText.Get(entry.State)} · {FormatText(entry.Format)} · {entry.CreatedAt.LocalDateTime:t}";
        OutputPath = entry.OutputPath;
        SourceUrl = entry.SourceUrl.ToString();
        IsFailed = entry.State is DownloadJobState.Failed;
        Indent = new Thickness(indent, 0, 0, 0);
        SizeText = DescribeSize(entry.OutputPath);
    }

    public string Title { get; }
    public string Details { get; }
    public string OutputPath { get; }
    public string SourceUrl { get; }
    public bool IsFailed { get; }
    public Thickness Indent { get; }
    public string SizeText { get; }

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
