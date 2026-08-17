using System.Globalization;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Processes;

public static class YtDlpProgressParser
{
    public static DownloadProgress? TryParse(string line)
    {
        const string prefix = "download:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line[prefix.Length..].Trim().Split('|');
        if (parts.Length != 4)
        {
            return null;
        }

        var percentText = parts[0].Trim().TrimEnd('%');
        double? percent = double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        TimeSpan? eta = TimeSpan.TryParse(parts[3].Trim(), CultureInfo.InvariantCulture, out var parsedEta) ? parsedEta : null;
        return new DownloadProgress(percent, parts[1].Trim(), parts[2].Trim(), eta, DownloadJobState.Downloading);
    }
}
