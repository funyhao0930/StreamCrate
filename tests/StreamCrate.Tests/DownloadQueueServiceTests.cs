using StreamCrate.Core.Models;
using StreamCrate.Infrastructure.Queue;

namespace StreamCrate.Tests;

public sealed class DownloadQueueServiceTests
{
    [Fact]
    public async Task Queue_runs_jobs_in_fifo_order_and_never_starts_two_together()
    {
        var started = new List<string>();
        var firstCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new DownloadQueueService(async (job, cancellationToken) =>
        {
            started.Add(job.Request.Media.MediaId);
            if (job.Request.Media.MediaId == "first")
            {
                await firstCanFinish.Task.WaitAsync(cancellationToken);
            }
        });

        await queue.EnqueueAsync(CreateRequest("first"));
        await queue.EnqueueAsync(CreateRequest("second"));
        await WaitUntilAsync(() => started.Count == 1);

        Assert.Equal(["first"], started);

        firstCanFinish.SetResult();
        await WaitUntilAsync(() => started.Count == 2);

        Assert.Equal(["first", "second"], started);
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task Queue_preserves_the_failure_message_for_the_user_interface()
    {
        var failed = new TaskCompletionSource<DownloadJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new DownloadQueueService((_, _) => throw new InvalidOperationException("FFmpeg 找不到可合併的音訊串流。"));
        queue.JobChanged += (_, job) =>
        {
            if (job.State == DownloadJobState.Failed)
            {
                failed.TrySetResult(job);
            }
        };

        await queue.EnqueueAsync(CreateRequest("failed"));
        var job = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("FFmpeg 找不到可合併的音訊串流。", job.ErrorMessage);
        await queue.DisposeAsync();
    }

    private static DownloadRequest CreateRequest(string id) => DownloadRequest.CreateVideo(
        new MediaItem("test", id, id, new Uri($"https://example.test/{id}"), null),
        @"C:\Downloads",
        VideoQuality.Best);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
