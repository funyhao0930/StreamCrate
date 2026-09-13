using System.Globalization;

namespace StreamCrate.App.Presentation;

/// <summary>
/// Parses the human readable transfer rates yt-dlp writes on its progress lines
/// (for example "1.23MiB/s" or "845.00KiB/s") into megabytes per second.
/// </summary>
internal static class TransferRate
{
    public static double ToMegabytesPerSecond(string? speed)
    {
        if (string.IsNullOrWhiteSpace(speed))
        {
            return 0;
        }

        var text = speed.Trim();
        var slashIndex = text.IndexOf('/');
        if (slashIndex >= 0)
        {
            text = text[..slashIndex];
        }

        var digits = 0;
        while (digits < text.Length && (char.IsDigit(text[digits]) || text[digits] is '.' or ','))
        {
            digits++;
        }

        if (digits == 0)
        {
            return 0;
        }

        if (!double.TryParse(text[..digits].Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        var unit = text[digits..].Trim().ToUpperInvariant();
        var bytesPerSecond = unit switch
        {
            "" or "B" => value,
            "K" or "KB" or "KIB" => value * 1024,
            "M" or "MB" or "MIB" => value * 1024 * 1024,
            "G" or "GB" or "GIB" => value * 1024 * 1024 * 1024,
            _ => 0,
        };

        return bytesPerSecond / 1024 / 1024;
    }

    /// <summary>
    /// Maps a rolling sample buffer onto a polyline inside the given box, newest sample at the right edge.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> BuildSparkline(IReadOnlyList<double> samples, double width, double height)
    {
        if (samples.Count < 2 || width <= 0 || height <= 0)
        {
            return [];
        }

        var peak = Math.Max(samples.Max(), 0.001);
        var step = width / (samples.Count - 1);
        var points = new List<(double X, double Y)>(samples.Count);
        for (var index = 0; index < samples.Count; index++)
        {
            var y = height - (Math.Clamp(samples[index] / peak, 0, 1) * height);
            points.Add((index * step, y));
        }

        return points;
    }
}
