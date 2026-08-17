using System.Threading.Channels;
using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Queue;

public sealed class DownloadQueueService : IAsyncDisposable
{
    private readonly Channel<DownloadJob> _jobs = Channel.CreateUnbounded<DownloadJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<DownloadJob, CancellationToken, Task> _execute;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    public DownloadQueueService(Func<DownloadJob, CancellationToken, Task> execute)
    {
        _execute = execute;
        _worker = Task.Run(WorkAsync);
    }

    public event EventHandler<DownloadJob>? JobChanged;

    public async Task<DownloadJob> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        var job = new DownloadJob(request);
        await _jobs.Writer.WriteAsync(job, cancellationToken);
        JobChanged?.Invoke(this, job);
        return job;
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(_shutdown.Token))
            {
                job.SetState(DownloadJobState.Downloading);
                JobChanged?.Invoke(this, job);
                try
                {
                    await _execute(job, _shutdown.Token);
                    job.SetState(DownloadJobState.Completed);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    job.SetState(DownloadJobState.Cancelled);
                }
                catch
                {
                    job.SetState(DownloadJobState.Failed);
                }

                JobChanged?.Invoke(this, job);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.Writer.TryComplete();
        _shutdown.Cancel();
        await _worker;
        _shutdown.Dispose();
    }
}
