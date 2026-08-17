using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Processes;

namespace StreamCrate.Tests;

public sealed class YtDlpCommandFactoryTests
{
    [Fact]
    public void Mp4_1080_request_prefers_compatible_video_and_audio_streams()
    {
        var request = DownloadRequest.CreateVideo(
            new MediaItem("youtube", "abc123", "A permitted video", new Uri("https://example.test/watch?v=abc123"), TimeSpan.FromMinutes(2)),
            @"C:\Downloads",
            VideoQuality.P1080);

        var specification = new YtDlpCommandFactory().CreateDownload(request, @"C:\tools\ffmpeg");

        Assert.Contains(
            specification.Arguments,
            argument => argument.StartsWith("bv*[height<=1080][ext=mp4]+ba[ext=m4a]/b[height<=1080][ext=mp4]", StringComparison.Ordinal));
        Assert.Contains("--merge-output-format", specification.Arguments);
        Assert.Contains("mp4", specification.Arguments);
    }

    [Fact]
    public void Cookies_file_is_passed_to_process_but_redacted_from_display_arguments()
    {
        var request = DownloadRequest.CreateVideo(
            new MediaItem("youtube", "abc123", "A permitted video", new Uri("https://example.test/watch?v=abc123"), TimeSpan.FromMinutes(2)),
            @"C:\Downloads",
            VideoQuality.P1080) with
        {
            Cookies = new CookieSelection(CookieSource.CookiesFile, @"C:\secrets\cookies.txt"),
        };

        var specification = new YtDlpCommandFactory().CreateDownload(request, @"C:\tools\ffmpeg");

        Assert.Contains(@"C:\secrets\cookies.txt", specification.Arguments);
        Assert.DoesNotContain(@"C:\secrets\cookies.txt", specification.RedactedDisplayArguments);
        Assert.Contains("<redacted-cookie-file>", specification.RedactedDisplayArguments);
    }
}
