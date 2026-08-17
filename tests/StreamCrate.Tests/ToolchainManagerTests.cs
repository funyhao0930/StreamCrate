using System.Net;
using System.Security.Cryptography;
using System.Text;
using StreamCrate.Infrastructure.Tooling;

namespace StreamCrate.Tests;

public sealed class ToolchainManagerTests
{
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
        Assert.True(File.Exists(manager.YtDlpPath));
    }

    private sealed class CoordinatedToolDownloadHandler : HttpMessageHandler
    {
        private const string DownloadUrl = "https://example.test/yt-dlp.exe";
        private static readonly byte[] Executable = Encoding.UTF8.GetBytes("yt-dlp test binary");
        private readonly TaskCompletionSource _downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ytDlpDownloadCount;

        public int YtDlpDownloadCount => _ytDlpDownloadCount;

        public Task WaitForDownloadStartAsync() => _downloadStarted.Task;

        public void ReleaseDownload() => _releaseDownload.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest")
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

            if (url == DownloadUrl)
            {
                Interlocked.Increment(ref _ytDlpDownloadCount);
                _downloadStarted.TrySetResult();
                await _releaseDownload.Task.WaitAsync(cancellationToken);
                return Bytes(Executable);
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
    }
}
