using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamCrate.Infrastructure.Updates;

public sealed record AppUpdateInfo(
    Version Version,
    string TagName,
    string? ReleaseNotes,
    Uri ReleasePageUrl,
    Uri InstallerUrl,
    Uri ChecksumUrl);

public sealed class AppUpdateService
{
    public const string ReleasesApi = "https://api.github.com/repos/funyhao0930/StreamCrate/releases/latest";
    public const string InstallerAssetName = "StreamCrate-Setup-x64.exe";
    public const string ChecksumAssetName = InstallerAssetName + ".sha256";
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;

    public AppUpdateService(HttpClient httpClient, string? downloadDirectory = null)
    {
        _httpClient = httpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StreamCrate/1.0");
        }

        _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "StreamCrate", "updates");
    }

    /// <summary>Returns the latest published release when it is newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    public async Task<AppUpdateInfo?> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = release.RootElement;

        if ((root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
            (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
        {
            return null;
        }

        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tagName, out var latestVersion) || Normalize(latestVersion) <= Normalize(currentVersion))
        {
            return null;
        }

        var installerUrl = FindAssetUrl(root, InstallerAssetName);
        var checksumUrl = FindAssetUrl(root, ChecksumAssetName);
        if (installerUrl is null || checksumUrl is null)
        {
            return null;
        }

        var releaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        var releasePage = new Uri(root.GetProperty("html_url").GetString()!);
        return new AppUpdateInfo(latestVersion, tagName, releaseNotes, releasePage, installerUrl, checksumUrl);
    }

    /// <summary>Downloads the installer and verifies it against the release's published SHA-256 before returning its path.</summary>
    public async Task<string> DownloadInstallerAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var checksumContent = await _httpClient.GetStringAsync(update.ChecksumUrl, cancellationToken);
        var expectedHash = ParseChecksum(checksumContent);

        Directory.CreateDirectory(_downloadDirectory);
        var destinationPath = Path.Combine(_downloadDirectory, $"StreamCrate-Setup-{update.Version}.exe");
        var temporaryPath = destinationPath + ".download";
        try
        {
            using (var response = await _httpClient.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(temporaryPath);
                var buffer = new byte[81920];
                long receivedBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    receivedBytes += read;
                    if (totalBytes is > 0)
                    {
                        progress?.Report((double)receivedBytes / totalBytes.Value);
                    }
                }
            }

            string actualHash;
            await using (var verificationStream = File.OpenRead(temporaryPath))
            {
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verificationStream, cancellationToken));
            }

            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新安裝檔的 SHA-256 驗證失敗，已取消更新。");
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            progress?.Report(1);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Starts the Inno Setup installer silently; it closes this app, upgrades in place, and relaunches it.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1",
            UseShellExecute = true,
        });
    }

    public static bool TryParseVersion(string tagName, out Version version)
    {
        var text = tagName.Trim().TrimStart('v', 'V');
        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        if (Version.TryParse(text, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    // 1.0.3 and 1.0.3.0 must compare equal: assembly versions always carry four parts.
    private static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static Uri? FindAssetUrl(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets))
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == name)
            {
                return new Uri(asset.GetProperty("browser_download_url").GetString()!);
            }
        }

        return null;
    }

    private static string ParseChecksum(string content)
    {
        var token = content.Trim().TrimStart('﻿').Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("找不到更新安裝檔的 SHA-256 雜湊。");
        }

        return token;
    }
}
