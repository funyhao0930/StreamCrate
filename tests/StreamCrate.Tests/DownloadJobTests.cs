using StreamCrate.Core.Models;

namespace StreamCrate.Tests;

public sealed class DownloadJobTests
{
    [Fact]
    public void New_job_starts_queued_until_the_queue_runs_it()
    {
        var request = DownloadRequest.CreateVideo(
            new MediaItem("youtube", "abc123", "A permitted video", new Uri("https://example.test/watch?v=abc123"), TimeSpan.FromMinutes(2)),
            @"C:\Downloads",
            VideoQuality.P1080);

        var job = new DownloadJob(request);

        Assert.Equal(DownloadJobState.Queued, job.State);
    }
}
