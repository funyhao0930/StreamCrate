using StreamCrate.Core.Models;

namespace StreamCrate.Infrastructure.Queue;

public sealed class DownloadQueueService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly List<DownloadJob> _pending = [];
    private readonly Func<DownloadJob, IProgress<DownloadProgress>, CancellationToken, Task> _execute;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly Task _worker;
    private DownloadJob? _active;
    private CancellationTokenSource? _activeCancellation;
    private bool _disposed;

    public DownloadQueueService(Func<DownloadJob, CancellationToken, Task> execute)
        : this((job, _, cancellationToken) => execute(job, cancellationToken))
    {
    }

    public DownloadQueueService(Func<DownloadJob, IProgress<DownloadProgress>, CancellationToken, Task> execute)
    {
        _execute = execute;
        _worker = Task.Run(WorkAsync);
    }

    public event EventHandler<DownloadJob>? JobChanged;

    public async Task<DownloadJob> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = new DownloadJob(request);
        lock (_gate)
        {
            ThrowIfDisposed();
            _jobs.Add(job);
            _pending.Add(job);
        }

        JobChanged?.Invoke(this, job);
        _workAvailable.Release();
        await Task.CompletedTask;
        return job;
    }

    public bool Cancel(Guid jobId)
    {
        DownloadJob? changed = null;
        lock (_gate)
        {
            var queued = _pending.FirstOrDefault(job => job.Id == jobId);
            if (queued is not null)
            {
                _pending.Remove(queued);
                queued.SetState(DownloadJobState.Cancelled);
                changed = queued;
            }
            else if (_active?.Id == jobId && _activeCancellation is not null)
            {
                _activeCancellation.Cancel();
                return true;
            }
        }

        if (changed is null)
        {
            return false;
        }

        JobChanged?.Invoke(this, changed);
        return true;
    }

    public IReadOnlyList<DownloadJob> GetSnapshot()
    {
        lock (_gate)
        {
            return _jobs.ToArray();
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                await _workAvailable.WaitAsync(_shutdown.Token);
                DownloadJob? job;
                CancellationTokenSource? cancellation;
                lock (_gate)
                {
                    job = _pending.Count == 0 ? null : _pending[0];
                    if (job is null)
                    {
                        continue;
                    }

                    _pending.RemoveAt(0);
                    _active = job;
                    _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    cancellation = _activeCancellation;
                    job.SetState(DownloadJobState.Downloading);
                }

                JobChanged?.Invoke(this, job);
                var progress = new Progress<DownloadProgress>(value =>
                {
                    lock (_gate)
                    {
                        job.SetProgress(value);
                    }

                    JobChanged?.Invoke(this, job);
                });
                try
                {
                    await _execute(job, progress, cancellation.Token);
                    job.SetState(DownloadJobState.Completed);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    job.SetState(DownloadJobState.Cancelled);
                }
                catch (InvalidOperationException)
                {
                    job.SetState(DownloadJobState.Failed, "下載工具錯誤");
                }
                catch
                {
                    job.SetState(DownloadJobState.Failed, "未預期錯誤");
                }
                finally
                {
                    lock (_gate)
                    {
                        _active = null;
                        _activeCancellation?.Dispose();
                        _activeCancellation = null;
                    }
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
        DownloadJob[] cancelled;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancelled = _pending.ToArray();
            _pending.Clear();
            foreach (var job in cancelled)
            {
                job.SetState(DownloadJobState.Cancelled);
            }

            _activeCancellation?.Cancel();
            _shutdown.Cancel();
        }

        foreach (var job in cancelled)
        {
            JobChanged?.Invoke(this, job);
        }

        await _worker;
        _workAvailable.Dispose();
        _shutdown.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
