using System.Net;
using System.Security.Cryptography;
using System.Text;
using StreamCrate.Infrastructure.Updates;

namespace StreamCrate.Tests;

public sealed class AppUpdateServiceTests
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("installer test binary");

    [Theory]
    [InlineData("V1.0.4", 1, 0, 4)]
    [InlineData("v2.3.0", 2, 3, 0)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    public void TryParseVersion_AcceptsReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(AppUpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenTagIsNewer_ReturnsRelease()
    {
        using var client = new HttpClient(new FakeReleaseHandler("V1.0.4", Hash(Installer)));
        var service = new AppUpdateService(client, NewTempDirectory());

        var update = await service.CheckForUpdateAsync(new Version(1, 0, 3, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 0, 4), update.Version);
    }

    [Theory]
    [InlineData("V1.0.3")]
    [InlineData("V1.0.2")]
    public async Task CheckForUpdateAsync_WhenTagIsNotNewer_ReturnsNull(string tag)
    {
        using var client = new HttpClient(new FakeReleaseHandler(tag, Hash(Installer)));
        var service = new AppUpdateService(client, NewTempDirectory());

        Assert.Null(await service.CheckForUpdateAsync(new Version(1, 0, 3, 0)));
    }

    [Fact]
    public async Task DownloadInstallerAsync_WithMatchingChecksum_ReturnsInstallerPath()
    {
        using var client = new HttpClient(new FakeReleaseHandler("V1.0.4", Hash(Installer)));
        var service = new AppUpdateService(client, NewTempDirectory());
        var update = await service.CheckForUpdateAsync(new Version(1, 0, 3));

        var path = await service.DownloadInstallerAsync(update!);

        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DownloadInstallerAsync_WithMismatchedChecksum_ThrowsAndLeavesNoFile()
    {
        var directory = NewTempDirectory();
        using var client = new HttpClient(new FakeReleaseHandler("V1.0.4", new string('0', 64)));
        var service = new AppUpdateService(client, directory);
        var update = await service.CheckForUpdateAsync(new Version(1, 0, 3));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(update!));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    private static string NewTempDirectory() => Path.Combine(Path.GetTempPath(), $"streamcrate-update-tests-{Guid.NewGuid():N}");

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private sealed class FakeReleaseHandler(string tagName, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpContent content = url switch
            {
                AppUpdateService.ReleasesApi => new StringContent($$"""
                    {
                      "tag_name": "{{tagName}}",
                      "draft": false,
                      "prerelease": false,
                      "html_url": "https://example.test/releases/{{tagName}}",
                      "body": "notes",
                      "assets": [
                        { "name": "StreamCrate-Setup-x64.exe", "browser_download_url": "https://example.test/setup.exe" },
                        { "name": "StreamCrate-Setup-x64.exe.sha256", "browser_download_url": "https://example.test/setup.exe.sha256" }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
                "https://example.test/setup.exe" => new ByteArrayContent(Installer),
                "https://example.test/setup.exe.sha256" => new StringContent($"{checksum} *StreamCrate-Setup-x64.exe"),
                _ => throw new InvalidOperationException($"Unexpected request: {url}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
