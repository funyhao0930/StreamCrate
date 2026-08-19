using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using StreamCrate.Infrastructure.Tooling;

namespace StreamCrate.Tests;

public sealed class ToolchainManagerTests
{
    [Fact]
    public async Task EnsureAvailableAsync_WhenLegacyStableExists_UsesSeparateNightlyExecutable()
    {
        var toolDirectory = Path.Combine(Path.GetTempPath(), $"streamcrate-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDirectory);
        await File.WriteAllTextAsync(Path.Combine(toolDirectory, "yt-dlp.exe"), "legacy stable binary");
        await File.WriteAllBytesAsync(Path.Combine(toolDirectory, "ffmpeg.exe"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(toolDirectory, "deno.exe"), [0]);

        var handler = new CoordinatedToolDownloadHandler();
        handler.ReleaseDownload();
        using var client = new HttpClient(handler);
        var manager = new ToolchainManager(client, toolDirectory);

        await manager.EnsureAvailableAsync();

        Assert.Equal("yt-dlp-nightly.exe", Path.GetFileName(manager.YtDlpPath));
        Assert.Equal("yt-dlp nightly test binary", await File.ReadAllTextAsync(manager.YtDlpPath));
        Assert.Equal("legacy stable binary", await File.ReadAllTextAsync(Path.Combine(toolDirectory, "yt-dlp.exe")));
    }

    [Fact]
    public async Task EnsureAvailableAsync_ConcurrentCalls_SharesOneYtDlpDownload()
    {
        var toolDirectory = Path.Combine(Path.GetTempPath(), $"streamcrate-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDirectory);
        await File.WriteAllBytesAsync(Path.Combine(toolDirectory, "ffmpeg.exe"), [0]);

        var handler = new CoordinatedToolDownloadHandler();
        using var client = new HttpClient(handler);
        var manager = new ToolchainManager(client, toolDirectory);

        var firstCall = manager.EnsureAvailableAsync();
        await handler.WaitForDownloadStartAsync();
        var secondCall = manager.EnsureAvailableAsync();
        handler.ReleaseDownload();
        await Task.WhenAll(firstCall, secondCall);

        Assert.Equal(1, handler.YtDlpDownloadCount);
        Assert.Equal(1, handler.DenoDownloadCount);
        Assert.True(File.Exists(manager.YtDlpPath));
        Assert.Equal("deno test binary", await File.ReadAllTextAsync(Path.Combine(toolDirectory, "deno.exe")));
    }

    private sealed class CoordinatedToolDownloadHandler : HttpMessageHandler
    {
        private const string DownloadUrl = "https://example.test/yt-dlp.exe";
        private const string DenoDownloadUrl = "https://example.test/deno-x86_64-pc-windows-msvc.zip";
        private static readonly byte[] Executable = Encoding.UTF8.GetBytes("yt-dlp nightly test binary");
        private static readonly byte[] DenoExecutable = Encoding.UTF8.GetBytes("deno test binary");
        private static readonly byte[] DenoArchive = CreateDenoArchive();
        private readonly TaskCompletionSource _downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ytDlpDownloadCount;
        private int _denoDownloadCount;

        public int YtDlpDownloadCount => _ytDlpDownloadCount;

        public int DenoDownloadCount => _denoDownloadCount;

        public Task WaitForDownloadStartAsync() => _downloadStarted.Task;

        public void ReleaseDownload() => _releaseDownload.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest")
            {
                return Json("""
                    {"assets":[
                      {"name":"yt-dlp.exe","browser_download_url":"https://example.test/yt-dlp.exe"},
                      {"name":"SHA2-256SUMS","browser_download_url":"https://example.test/SHA2-256SUMS"}
                    ]}
                    """);
            }

            if (url == "https://example.test/SHA2-256SUMS")
            {
                var hash = Convert.ToHexString(SHA256.HashData(Executable));
                return Text($"{hash}  yt-dlp.exe\n");
            }

            if (url == "https://api.github.com/repos/denoland/deno/releases/latest")
            {
                return Json("""
                    {"assets":[
                      {"name":"deno-x86_64-pc-windows-msvc.zip","browser_download_url":"https://example.test/deno-x86_64-pc-windows-msvc.zip"},
                      {"name":"deno-x86_64-pc-windows-msvc.zip.sha256sum","browser_download_url":"https://example.test/deno-x86_64-pc-windows-msvc.zip.sha256sum"}
                    ]}
                    """);
            }

            if (url == "https://example.test/deno-x86_64-pc-windows-msvc.zip.sha256sum")
            {
                var hash = Convert.ToHexString(SHA256.HashData(DenoArchive));
                return Text($"""
                    Algorithm : SHA256
                    Hash      : {hash}
                    Path      : C:\a\deno\deno\target\release\deno-x86_64-pc-windows-msvc.zip
                    """);
            }

            if (url == DownloadUrl)
            {
                Interlocked.Increment(ref _ytDlpDownloadCount);
                _downloadStarted.TrySetResult();
                await _releaseDownload.Task.WaitAsync(cancellationToken);
                return Bytes(Executable);
            }

            if (url == DenoDownloadUrl)
            {
                Interlocked.Increment(ref _denoDownloadCount);
                return Bytes(DenoArchive);
            }

            throw new InvalidOperationException($"Unexpected HTTP request: {url}");
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Text(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain"),
        };

        private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };

        private static byte[] CreateDenoArchive()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("deno.exe");
                using var destination = entry.Open();
                destination.Write(DenoExecutable);
            }

            return stream.ToArray();
        }
    }
}
