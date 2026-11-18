using System.Globalization;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Processes;

public static class YtDlpProgressParser
{
    /// <summary>
    /// Marker literal that <see cref="YtDlpCommandFactory"/> puts at the head of its progress
    /// template. The <c>download:</c> part of <c>--progress-template</c> is a template *type*
    /// selector that yt-dlp consumes, so it never reaches stdout; this marker is what actually
    /// separates a progress line from yt-dlp's ordinary chatter.
    /// </summary>
    public const string LinePrefix = "SCPROGRESS|";

    private static readonly string[] EtaFormats = ["mm\\:ss", "m\\:ss", "hh\\:mm\\:ss", "h\\:mm\\:ss"];

    public static DownloadProgress? TryParse(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = trimmed[LinePrefix.Length..].Split('|');
        if (parts.Length != 4)
        {
            return null;
        }

        var percentText = parts[0].Trim().TrimEnd('%');
        double? percent = double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        return new DownloadProgress(percent, Known(parts[1]), FormatTotal(parts[2]), ParseEta(parts[3]), DownloadJobState.Downloading);
    }

    /// <summary>The total arrives as a raw byte count; older templates sent yt-dlp's own text.</summary>
    private static string FormatTotal(string part)
    {
        if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
        {
            return Known(part);
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes:0.00}{units[unit]}");
    }

    /// <summary>yt-dlp writes "Unknown"/"NA" into its *_str fields before it has a figure.</summary>
    private static string Known(string part)
    {
        var text = part.Trim();
        return text.Length == 0 || text.Contains("Unknown", StringComparison.OrdinalIgnoreCase) || text is "NA" or "N/A"
            ? string.Empty
            : text;
    }

    /// <summary>
    /// yt-dlp's ETA is mm:ss until it passes an hour. TimeSpan.Parse reads a bare "00:50" as
    /// hh:mm, which would report 50 minutes left on a download with 50 seconds to go, so the
    /// accepted formats are spelled out instead.
    /// </summary>
    private static TimeSpan? ParseEta(string part)
    {
        var text = part.Trim();
        return TimeSpan.TryParseExact(text, EtaFormats, CultureInfo.InvariantCulture, out var eta) ? eta : null;
    }
}
