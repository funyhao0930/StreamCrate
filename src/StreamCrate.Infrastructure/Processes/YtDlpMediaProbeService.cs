using System.Diagnostics;
using System.Text.Json;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Processes;

public sealed class YtDlpMediaProbeService
{
    private readonly string _executablePath;
    private readonly YtDlpCommandFactory _commands = new();

    public YtDlpMediaProbeService(string executablePath)
    {
        _executablePath = executablePath;
    }

    public async Task<ProbeResult> ProbeAsync(Uri sourceUrl, CookieSelection cookies, CancellationToken cancellationToken = default)
    {
        var command = _commands.CreateProbe(sourceUrl, cookies);
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("無法啟動 yt-dlp。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "yt-dlp 無法解析此網址。" : Redact(error));
        }

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var items = entries.EnumerateArray().Select(entry => ToMediaItem(entry, sourceUrl)).ToArray();
            var title = GetString(root, "title") ?? "播放清單";
            var playlistId = GetString(root, "id") ?? title;
            return new ProbeResult(null, new PlaylistInfo(playlistId, title, items));
        }

        return new ProbeResult(ToMediaItem(root, sourceUrl), null);
    }

    private static MediaItem ToMediaItem(JsonElement item, Uri fallbackUrl)
    {
        var id = GetString(item, "id") ?? throw new InvalidDataException("yt-dlp 未回傳媒體 ID。");
        var extractor = GetString(item, "extractor_key") ?? GetString(item, "extractor") ?? "unknown";
        var url = GetString(item, "webpage_url");
        TimeSpan? duration = item.TryGetProperty("duration", out var durationElement) && durationElement.TryGetDouble(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
        var thumbnail = GetString(item, "thumbnail");
        return new MediaItem(
            extractor,
            id,
            GetString(item, "title") ?? id,
            Uri.TryCreate(url, UriKind.Absolute, out var source) ? source : fallbackUrl,
            duration,
            Uri.TryCreate(thumbnail, UriKind.Absolute, out var thumbnailUri) ? thumbnailUri : null);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Redact(string text) => text.Replace("--cookies", "<redacted-cookie-option>", StringComparison.OrdinalIgnoreCase);
}
