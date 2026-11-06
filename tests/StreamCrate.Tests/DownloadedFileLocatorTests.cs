using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Files;

namespace StreamCrate.Tests;

public sealed class DownloadedFileLocatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "StreamCrateLocator", Guid.NewGuid().ToString("N"));

    public DownloadedFileLocatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Finds_the_finished_video_and_ignores_leftovers_and_other_formats()
    {
        Touch("Song [abc123].f137.mp4");
        Touch("Song [abc123].mp4.part");
        Touch("Song [abc123].mp3");
        Touch("Other [zzz999].mp4");
        var expected = Touch("Song [abc123].mp4");

        Assert.Equal(expected, DownloadedFileLocator.Find(CreateRequest(DownloadFormat.Mp4)));
    }

    [Fact]
    public void Finds_the_mp3_when_audio_was_requested()
    {
        Touch("Song [abc123].mp4");
        var expected = Touch("Song [abc123].mp3");

        Assert.Equal(expected, DownloadedFileLocator.Find(CreateRequest(DownloadFormat.Mp3)));
    }

    [Fact]
    public void Looks_inside_the_playlist_folder_for_playlist_items()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "My_List"));
        var expected = Touch(Path.Combine("My_List", "03 - Song [abc123].mp4"));

        var request = CreateRequest(DownloadFormat.Mp4) with { PlaylistTitle = "My/List", PlaylistIndex = 3 };

        Assert.Equal(expected, DownloadedFileLocator.Find(request));
    }

    [Fact]
    public void Returns_null_when_the_file_is_gone()
    {
        Assert.Null(DownloadedFileLocator.Find(CreateRequest(DownloadFormat.Mp4)));
        Assert.Null(DownloadedFileLocator.Find(CreateRequest(DownloadFormat.Mp4) with { OutputDirectory = Path.Combine(_directory, "missing") }));
    }

    private DownloadRequest CreateRequest(DownloadFormat format) => new(
        new MediaItem("youtube", "abc123", "Song", new Uri("https://example.test/watch?v=abc123"), null),
        _directory,
        format,
        VideoQuality.Best,
        CookieSelection.None);

    private string Touch(string relativePath)
    {
        var path = Path.Combine(_directory, relativePath);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
