using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Processes;

public sealed record ProcessSpecification(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> RedactedDisplayArguments);

public sealed class YtDlpCommandFactory
{
    public ProcessSpecification CreateProbe(Uri sourceUrl, CookieSelection cookies)
    {
        var arguments = new List<string> { "--dump-single-json", "--no-warnings" };
        AddCookies(arguments, cookies);
        arguments.Add(sourceUrl.AbsoluteUri);
        return new ProcessSpecification("yt-dlp.exe", arguments, Redact(arguments, cookies));
    }

    public ProcessSpecification CreateDownload(DownloadRequest request, string ffmpegDirectory)
    {
        var arguments = new List<string>
        {
            "--newline",
            "--no-warnings",
            "--progress-template",
            "download:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._total_bytes_str)s|%(progress._eta_str)s",
            "--ffmpeg-location",
            ffmpegDirectory,
            "--continue",
            "--no-overwrites",
            "--paths",
            request.OutputDirectory,
            "--output",
            BuildOutputTemplate(request),
        };

        if (request.Format == DownloadFormat.Mp3)
        {
            arguments.AddRange(["--extract-audio", "--audio-format", "mp3", "--audio-quality", "0"]);
        }
        else
        {
            arguments.AddRange(["--format", BuildVideoFormat(request.Quality), "--merge-output-format", "mp4"]);
        }

        AddCookies(arguments, request.Cookies);
        arguments.Add(request.Media.SourceUrl.AbsoluteUri);
        return new ProcessSpecification("yt-dlp.exe", arguments, Redact(arguments, request.Cookies));
    }

    private static string BuildVideoFormat(VideoQuality quality)
    {
        var height = quality switch
        {
            VideoQuality.P2160 => 2160,
            VideoQuality.P1440 => 1440,
            VideoQuality.P1080 => 1080,
            VideoQuality.P720 => 720,
            _ => (int?)null,
        };

        return height is null
            ? "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b"
            : $"bv*[height<={height}][ext=mp4]+ba[ext=m4a]/b[height<={height}][ext=mp4]/bv*[height<={height}]+ba/b";
    }

    private static string BuildOutputTemplate(DownloadRequest request) =>
        request.PlaylistTitle is null
            ? "%(title)s [%(id)s].%(ext)s"
            : "%(playlist_title)s/%(playlist_index)02d - %(title)s [%(id)s].%(ext)s";

    private static void AddCookies(List<string> arguments, CookieSelection cookies)
    {
        switch (cookies.Source)
        {
            case CookieSource.Chrome:
                arguments.AddRange(["--cookies-from-browser", "chrome"]);
                break;
            case CookieSource.Edge:
                arguments.AddRange(["--cookies-from-browser", "edge"]);
                break;
            case CookieSource.Firefox:
                arguments.AddRange(["--cookies-from-browser", "firefox"]);
                break;
            case CookieSource.CookiesFile when !string.IsNullOrWhiteSpace(cookies.CookiesFilePath):
                arguments.AddRange(["--cookies", cookies.CookiesFilePath]);
                break;
        }
    }

    private static IReadOnlyList<string> Redact(IReadOnlyList<string> arguments, CookieSelection cookies)
    {
        var redacted = arguments.ToList();
        if (cookies.Source == CookieSource.CookiesFile && !string.IsNullOrWhiteSpace(cookies.CookiesFilePath))
        {
            var index = redacted.FindIndex(argument => string.Equals(argument, cookies.CookiesFilePath, StringComparison.Ordinal));
            if (index >= 0)
            {
                redacted[index] = "<redacted-cookie-file>";
            }
        }

        return redacted;
    }
}
