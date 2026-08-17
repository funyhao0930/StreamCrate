namespace StreamCrate.Core.Models;

public enum CookieSource
{
    None,
    Chrome,
    Edge,
    Firefox,
    CookiesFile,
}

public enum VideoQuality
{
    Best,
    P2160,
    P1440,
    P1080,
    P720,
}

public enum DownloadFormat
{
    Mp4,
    Mp3,
}

public enum DownloadJobState
{
    Queued,
    Probing,
    Downloading,
    PostProcessing,
    Completed,
    Failed,
    Cancelled,
    SkippedExisting,
}

public sealed record MediaItem(
    string Extractor,
    string MediaId,
    string Title,
    Uri SourceUrl,
    TimeSpan? Duration,
    Uri? ThumbnailUrl = null);

public sealed record PlaylistInfo(string PlaylistId, string Title, IReadOnlyList<MediaItem> Items);

public sealed record CookieSelection(CookieSource Source, string? CookiesFilePath = null)
{
    public static CookieSelection None { get; } = new(CookieSource.None);
}

public sealed record DownloadRequest(
    MediaItem Media,
    string OutputDirectory,
    DownloadFormat Format,
    VideoQuality Quality,
    CookieSelection Cookies,
    string? PlaylistTitle = null,
    int? PlaylistIndex = null)
{
    public static DownloadRequest CreateVideo(MediaItem media, string outputDirectory, VideoQuality quality) =>
        new(media, outputDirectory, DownloadFormat.Mp4, quality, CookieSelection.None);
}

public sealed class DownloadJob
{
    public DownloadJob(DownloadRequest request)
    {
        Request = request;
        State = DownloadJobState.Queued;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public DownloadRequest Request { get; }

    public DownloadJobState State { get; private set; }

    public void SetState(DownloadJobState state) => State = state;
}

public sealed record DownloadProgress(
    double? Percent,
    string? Speed,
    string? TotalSize,
    TimeSpan? Eta,
    DownloadJobState State);

public sealed record ToolVersion(string Name, string Version, string ExecutablePath);

public sealed record HistoryEntry(
    Guid Id,
    string Extractor,
    string MediaId,
    string Title,
    Uri SourceUrl,
    DownloadFormat Format,
    VideoQuality Quality,
    string OutputPath,
    DownloadJobState State,
    string? ErrorCategory,
    DateTimeOffset CreatedAt);
