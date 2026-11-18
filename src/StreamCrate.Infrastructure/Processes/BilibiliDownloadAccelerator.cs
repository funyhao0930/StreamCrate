using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Processes;

/// <summary>
/// Bilibili serves each stream as one file, and yt-dlp fetches that file over a single
/// connection, which Bilibili's CDN throttles to a few hundred KiB/s. Some of the hosts it hands
/// out are also slow or broken for a given viewer. This picks the fastest of Bilibili's mirrors
/// with a short race, then describes each stream to yt-dlp as an HLS playlist of byte ranges,
/// so <c>--concurrent-fragments</c> fetches it over several connections while yt-dlp keeps
/// doing progress, retries and merging. (yt-dlp's DASH downloader ignores byte ranges; its HLS
/// one honours <c>EXT-X-BYTERANGE</c>.)
/// </summary>
public sealed class BilibiliDownloadAccelerator
{
    public const long FragmentSize = 4 * 1024 * 1024;

    /// <summary>
    /// Bilibili's own mirrors, which accept a stream URL signed for another Bilibili host. Akamai
    /// is never a target: it rejects URLs signed for other hosts. The ali, hw and 08c mirrors
    /// stalled or crawled in testing.
    /// </summary>
    public static readonly IReadOnlyList<string> MirrorHosts =
    [
        "upos-sz-mirrorcosov.bilivideo.com",
        "upos-sz-mirrorcos.bilivideo.com",
        "upos-sz-estgoss.bilivideo.com",
    ];

    private const int RaceConnections = 3;
    private const int RaceRangeBytes = 1024 * 1024;
    private static readonly TimeSpan RaceDuration = TimeSpan.FromSeconds(3);

    private static readonly HttpClient RaceClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly string _ytDlpPath;
    private readonly YtDlpCommandFactory _commands = new();

    public BilibiliDownloadAccelerator(string ytDlpPath)
    {
        _ytDlpPath = ytDlpPath;
    }

    public static bool Applies(DownloadRequest request) =>
        request.Format == DownloadFormat.Mp4 && YtDlpCommandFactory.IsBilibili(request.Media.SourceUrl);

    /// <summary>
    /// Writes an info JSON for yt-dlp to download from, or returns null when anything about it
    /// fails, so the caller can fall back to a plain download. The file holds signed stream URLs
    /// and possibly cookies: it goes to the temp folder, and the caller deletes it.
    /// </summary>
    public async Task<AcceleratedSource?> TryPrepareAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var info = JsonNode.Parse(await DumpSelectedFormatsAsync(request, cancellationToken)) as JsonObject;
            if (info is null)
            {
                return null;
            }

            var streams = GetRequestedStreams(info);
            if (streams.Count == 0)
            {
                return null;
            }

            var winners = await Task.WhenAll(streams.Select(stream => RaceAsync(stream, cancellationToken)));
            if (winners.Any(winner => winner is null))
            {
                return null;
            }

            var chosen = new Dictionary<string, ChosenStream>(StringComparer.Ordinal);
            for (var index = 0; index < streams.Count; index++)
            {
                chosen[streams[index].FormatId] = winners[index]!;
            }

            var formatIds = Rewrite(info, chosen);
            var directory = Path.Combine(Path.GetTempPath(), "StreamCrate");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"bilibili-{Guid.NewGuid():N}.info.json");
            await File.WriteAllTextAsync(path, info.ToJsonString(), cancellationToken);
            return new AcceleratedSource(path, formatIds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The streams yt-dlp picked: <c>requested_formats</c> for video+audio, else the entry itself.</summary>
    public static IReadOnlyList<RequestedStream> GetRequestedStreams(JsonObject info)
    {
        var entries = info["requested_formats"] is JsonArray requested
            ? requested.OfType<JsonObject>().ToList()
            : [info];

        var streams = new List<RequestedStream>();
        foreach (var entry in entries)
        {
            var formatId = entry["format_id"]?.GetValue<string>();
            var url = entry["url"]?.GetValue<string>();
            var protocol = entry["protocol"]?.GetValue<string>();
            if (formatId is null || url is null || protocol is not ("https" or "http"))
            {
                return [];
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry["http_headers"] is JsonObject headerObject)
            {
                foreach (var (name, value) in headerObject)
                {
                    if (value?.GetValueKind() == JsonValueKind.String)
                    {
                        headers[name] = value.GetValue<string>();
                    }
                }
            }

            streams.Add(new RequestedStream(formatId, url, headers));
        }

        return streams;
    }

    /// <summary>The original URL first, then the same signed path on each Bilibili mirror.</summary>
    public static IReadOnlyList<string> CandidateUrls(string originalUrl)
    {
        var original = new Uri(originalUrl);
        var candidates = new List<string> { originalUrl };
        foreach (var host in MirrorHosts)
        {
            if (!host.Equals(original.Host, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new UriBuilder(original) { Host = host, Port = -1 }.Uri.AbsoluteUri);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Points the chosen formats at byte-range playlists and returns the <c>--format</c> value
    /// that selects exactly them.
    /// </summary>
    public static string Rewrite(JsonObject info, IReadOnlyDictionary<string, ChosenStream> chosen)
    {
        var targets = info["formats"] is JsonArray formats
            ? formats.OfType<JsonObject>().ToList()
            : [];
        if (info["requested_formats"] is null)
        {
            // A single combined stream: the entry itself is also what gets downloaded.
            targets.Add(info);
        }

        foreach (var format in targets)
        {
            var formatId = format["format_id"]?.GetValue<string>();
            if (formatId is null || !chosen.TryGetValue(formatId, out var stream))
            {
                continue;
            }

            format["url"] = "data:application/vnd.apple.mpegurl;base64," +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPlaylist(stream.Url, stream.Size)));
            format["protocol"] = "m3u8_native";
            format["filesize"] = stream.Size;
            format.Remove("fragments");
        }

        info.Remove("requested_formats");
        return string.Join('+', chosen.Keys);
    }

    public static string BuildPlaylist(string url, long size, long fragmentSize = FragmentSize)
    {
        var builder = new StringBuilder()
            .Append("#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (long start = 0; start < size; start += fragmentSize)
        {
            var length = Math.Min(fragmentSize, size - start);
            builder.Append("#EXTINF:10,\n")
                .Append("#EXT-X-BYTERANGE:").Append(length).Append('@').Append(start).Append('\n')
                .Append(url).Append('\n');
        }

        return builder.Append("#EXT-X-ENDLIST\n").ToString();
    }

    private async Task<string> DumpSelectedFormatsAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        var command = _commands.CreateSelectedFormatsProbe(request);
        var startInfo = new ProcessStartInfo(_ytDlpPath)
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
        using var registration = cancellationToken.Register(() =>
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        await errorTask;
        return process.ExitCode == 0 ? output : throw new InvalidOperationException("yt-dlp 無法解析 B 站串流。");
    }

    /// <summary>Downloads a little from every candidate at once and keeps the fastest that worked.</summary>
    private static async Task<ChosenStream?> RaceAsync(RequestedStream stream, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(CandidateUrls(stream.Url).Select(url => MeasureAsync(url, stream.Headers, cancellationToken)));
        return results
            .Where(result => result is not null && result.BytesPerSecond > 0)
            .OrderByDescending(result => result!.BytesPerSecond)
            .Select(result => new ChosenStream(result!.Url, result.Size))
            .FirstOrDefault();
    }

    private static async Task<RaceResult?> MeasureAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RaceDuration);
        var stopwatch = Stopwatch.StartNew();
        var reads = await Task.WhenAll(Enumerable.Range(0, RaceConnections)
            .Select(index => ReadRangeAsync(url, headers, (long)index * RaceRangeBytes, deadline.Token)));
        var elapsed = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.01);
        cancellationToken.ThrowIfCancellationRequested();

        var size = reads.Select(read => read.Size).FirstOrDefault(value => value > 0);
        var bytes = reads.Sum(read => read.Bytes);
        return size > 0 ? new RaceResult(url, size, bytes / elapsed) : null;
    }

    private static async Task<(long Bytes, long Size)> ReadRangeAsync(
        string url, IReadOnlyDictionary<string, string> headers, long start, CancellationToken cancellationToken)
    {
        long read = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + RaceRangeBytes - 1);
            using var response = await RaceClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not { } size)
            {
                return (0, 0);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[64 * 1024];
            try
            {
                int count;
                while ((count = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    read += count;
                }
            }
            catch (OperationCanceledException)
            {
                // The race window closed mid-read; what arrived so far still counts.
            }

            return (read, size);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return (0, 0);
        }
    }

    public sealed record RequestedStream(string FormatId, string Url, IReadOnlyDictionary<string, string> Headers);

    public sealed record ChosenStream(string Url, long Size);

    private sealed record RaceResult(string Url, long Size, double BytesPerSecond);
}
