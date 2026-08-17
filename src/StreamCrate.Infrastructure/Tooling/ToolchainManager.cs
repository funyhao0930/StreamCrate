using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Tooling;

public sealed class ToolchainManager
{
    private const string YtDlpReleasesApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string FfmpegReleasesApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly object _ensureLock = new();
    private readonly string _toolDirectory;
    private Task<IReadOnlyList<ToolVersion>>? _ensureTask;

    public ToolchainManager(HttpClient httpClient, string? toolDirectory = null)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StreamCrate/0.1");
        _toolDirectory = toolDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamCrate", "tools");
    }

    public string YtDlpPath => Path.Combine(_toolDirectory, "yt-dlp.exe");

    public string FfmpegPath => Path.Combine(_toolDirectory, "ffmpeg.exe");

    public async Task<IReadOnlyList<ToolVersion>> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<ToolVersion>> ensureTask;
        lock (_ensureLock)
        {
            _ensureTask ??= EnsureAvailableCoreAsync();
            ensureTask = _ensureTask;
        }

        try
        {
            return await ensureTask.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (_ensureLock)
            {
                if (ReferenceEquals(_ensureTask, ensureTask) && ensureTask.IsFaulted)
                {
                    _ensureTask = null;
                }
            }

            throw;
        }
    }

    private async Task<IReadOnlyList<ToolVersion>> EnsureAvailableCoreAsync()
    {
        Directory.CreateDirectory(_toolDirectory);
        if (!File.Exists(YtDlpPath))
        {
            await DownloadYtDlpAsync(CancellationToken.None);
        }

        if (!File.Exists(FfmpegPath))
        {
            await DownloadFfmpegAsync(CancellationToken.None);
        }

        return
        [
            new ToolVersion("yt-dlp", "managed", YtDlpPath),
            new ToolVersion("ffmpeg", "managed", FfmpegPath),
        ];
    }

    private async Task DownloadYtDlpAsync(CancellationToken cancellationToken)
    {
        using var release = await GetReleaseAsync(YtDlpReleasesApi, cancellationToken);
        var executableUrl = FindAssetUrl(release.RootElement, "yt-dlp.exe");
        var sumsUrl = FindAssetUrl(release.RootElement, "SHA2-256SUMS");
        var sums = await _httpClient.GetStringAsync(sumsUrl, cancellationToken);
        var expectedHash = FindChecksum(sums, "yt-dlp.exe");
        await DownloadVerifiedFileAsync(executableUrl, expectedHash, YtDlpPath, cancellationToken);
    }

    private async Task DownloadFfmpegAsync(CancellationToken cancellationToken)
    {
        using var release = await GetReleaseAsync(FfmpegReleasesApi, cancellationToken);
        var archiveUrl = FindAssetUrl(release.RootElement, "ffmpeg-master-latest-win64-lgpl.zip");
        var checksumsUrl = FindAssetUrl(release.RootElement, "checksums.sha256");
        var checksums = await _httpClient.GetStringAsync(checksumsUrl, cancellationToken);
        var expectedHash = FindChecksum(checksums, "ffmpeg-master-latest-win64-lgpl.zip");
        var archivePath = Path.Combine(_toolDirectory, "ffmpeg.zip.download");
        await DownloadVerifiedFileAsync(archiveUrl, expectedHash, archivePath, cancellationToken);

        var extractDirectory = Path.Combine(_toolDirectory, $"ffmpeg-extract-{Guid.NewGuid():N}");
        ZipFile.ExtractToDirectory(archivePath, extractDirectory);
        var sourcePath = Directory.EnumerateFiles(extractDirectory, "ffmpeg.exe", SearchOption.AllDirectories).Single();
        var temporaryPath = FfmpegPath + ".download";
        File.Copy(sourcePath, temporaryPath, overwrite: true);
        File.Move(temporaryPath, FfmpegPath, overwrite: true);
        File.Delete(archivePath);
    }

    private async Task DownloadVerifiedFileAsync(Uri source, string expectedHash, string destinationPath, CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + ".download";
        await using (var sourceStream = await _httpClient.GetStreamAsync(source, cancellationToken))
        await using (var destinationStream = File.Create(temporaryPath))
        {
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }

        string actualHash;
        await using (var verificationStream = File.OpenRead(temporaryPath))
        {
            actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verificationStream, cancellationToken));
        }

        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException("工具檔案的 SHA-256 驗證失敗。");
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private async Task<JsonDocument> GetReleaseAsync(string endpoint, CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(endpoint, cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static Uri FindAssetUrl(JsonElement release, string name)
    {
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == name)
            {
                return new Uri(asset.GetProperty("browser_download_url").GetString()!);
            }
        }

        throw new InvalidDataException($"找不到上游資產：{name}");
    }

    private static string FindChecksum(string content, string assetName)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*') == assetName)
            {
                return parts[0];
            }
        }

        throw new InvalidDataException($"找不到 {assetName} 的 SHA-256 雜湊。");
    }
}
