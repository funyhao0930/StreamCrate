using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Queue;

namespace StreamCrate.Tests;

public sealed class DownloadQueueCancellationTests
{
    [Fact]
    public async Task Cancel_removes_a_waiting_job_without_starting_it()
    {
        var started = new List<string>();
        var firstCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DownloadQueueService(async (job, _, cancellationToken) =>
        {
            started.Add(job.Request.Media.MediaId);
            if (job.Request.Media.MediaId == "first")
            {
                await firstCanFinish.Task.WaitAsync(cancellationToken);
            }
        });

        await queue.EnqueueAsync(CreateRequest("first"));
        var second = await queue.EnqueueAsync(CreateRequest("second"));
        await WaitUntilAsync(() => started.Count == 1);

        Assert.True(queue.Cancel(second.Id));
        firstCanFinish.SetResult();
        await WaitUntilAsync(() => queue.GetSnapshot().All(job => job.State is DownloadJobState.Completed or DownloadJobState.Cancelled));

        Assert.Equal(["first"], started);
        Assert.Equal(DownloadJobState.Cancelled, queue.GetSnapshot().Single(job => job.Id == second.Id).State);
    }

    [Fact]
    public async Task Cancel_stops_the_active_job_and_reports_cancelled_state()
    {
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DownloadQueueService(async (job, _, cancellationToken) =>
        {
            started.SetResult(job.Id);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        var job = await queue.EnqueueAsync(CreateRequest("active"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(queue.Cancel(job.Id));
        await WaitUntilAsync(() => queue.GetSnapshot().Single().State == DownloadJobState.Cancelled);

        Assert.Equal(DownloadJobState.Cancelled, queue.GetSnapshot().Single().State);
    }

    private static DownloadRequest CreateRequest(string id) => DownloadRequest.CreateVideo(
        new MediaItem("test", id, id, new Uri($"https://example.test/{id}"), null),
        @"C:\Downloads", VideoQuality.Best);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
