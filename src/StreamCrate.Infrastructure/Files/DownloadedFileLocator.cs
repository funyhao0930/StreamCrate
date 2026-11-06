using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Processes;

namespace StreamCrate.Infrastructure.Files;

/// <summary>
/// Finds the file a finished download left on disk. The queue only remembers the output folder, but
/// every download is named "… [&lt;id&gt;].&lt;ext&gt;" (inside the playlist's folder for playlist items), so
/// the media id and the requested format pin the file down without guessing from titles.
/// </summary>
public static class DownloadedFileLocator
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "webm", "mov", "m4v", "flv",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3",
    };

    /// <summary>Full path of the downloaded file, or null when it is no longer where it was saved.</summary>
    public static string? Find(DownloadRequest request)
    {
        var directory = request.PlaylistTitle is null
            ? request.OutputDirectory
            : Path.Combine(request.OutputDirectory, YtDlpCommandFactory.GetPlaylistFolderName(request.PlaylistTitle));

        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory.EnumerateFiles(directory)
                .Where(path => IsDownloadOf(Path.GetFileName(path), request))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for "… [id].ext" with a final media extension; yt-dlp's leftovers such as
    /// "[id].f137.mp4" or "[id].mp4.part" do not count as the download.
    /// </summary>
    public static bool IsDownloadOf(string fileName, DownloadRequest request)
    {
        var marker = $"[{request.Media.MediaId}].";
        var markerIndex = fileName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var extension = fileName[(markerIndex + marker.Length)..];
        if (extension.Length == 0 || extension.Contains('.'))
        {
            return false;
        }

        var allowed = request.Format == DownloadFormat.Mp3 ? AudioExtensions : VideoExtensions;
        return allowed.Contains(extension);
    }
}
