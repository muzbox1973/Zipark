using System.ComponentModel;
using System.Runtime.CompilerServices;
using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.Application.Jobs;

/// <summary>Observable state of one queued job (bound directly by the UI).</summary>
public sealed class JobItem : INotifyPropertyChanged, IDisposable
{
    private JobStage _stage = JobStage.Queued;
    private JobProgress? _progress;
    private JobResult? _result;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<JobResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal JobItem(int id, JobRequest request, SecretPassword? password)
    {
        Id = id;
        Request = request;
        Password = password;
        Title = request.Describe();
        Created = DateTime.Now;
    }

    public int Id { get; }
    public JobRequest Request { get; }
    internal SecretPassword? Password { get; private set; }
    public string Title { get; }
    public DateTime Created { get; }
    public DateTime? Started { get; private set; }
    public DateTime? Finished { get; private set; }
    public Task<JobResult> Completion => _done.Task;
    internal CancellationToken Token => _cts.Token;

    public JobStage Stage
    {
        get => _stage;
        internal set => Set(ref _stage, value);
    }

    public JobProgress? Progress
    {
        get => _progress;
        internal set
        {
            Set(ref _progress, value);
            OnPropertyChanged(nameof(Percent));
        }
    }

    public double Percent => _progress?.Percent ?? (_stage == JobStage.Completed ? 100 : 0);

    public JobResult? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public bool IsActive => _stage is JobStage.Queued or JobStage.Preparing or JobStage.Scanning or JobStage.Running or JobStage.Verifying or JobStage.Finalizing;

    public void Cancel()
    {
        if (!IsActive) return;
        _cts.Cancel();
        if (_stage == JobStage.Queued) Complete(new JobResult { Success = false, Error = ErrorCode.Cancelled, Message = "Cancelled." });
    }

    internal void MarkStarted()
    {
        Started = DateTime.Now;
        Stage = JobStage.Running;
    }

    internal void Complete(JobResult result)
    {
        if (_done.Task.IsCompleted) return;
        Finished = DateTime.Now;
        Result = result;
        Stage = result.Success ? JobStage.Completed : result.Error == ErrorCode.Cancelled ? JobStage.Cancelled : JobStage.Failed;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Percent));
        Password?.Dispose();
        Password = null;
        _done.TrySetResult(result);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(Stage)) OnPropertyChanged(nameof(IsActive));
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _cts.Dispose();
        Password?.Dispose();
    }
}

/// <summary>FIFO job queue with a configurable number of concurrent jobs.</summary>
public sealed class JobQueue : IDisposable
{
    private readonly IJobExecutor _executor;
    private readonly object _gate = new();
    private readonly List<JobItem> _items = [];
    private readonly Queue<JobItem> _pending = new();
    private int _running;
    private int _nextId = 1;

    public JobQueue(IJobExecutor executor, int maxParallel = 1)
    {
        _executor = executor;
        MaxParallel = Math.Max(1, maxParallel);
    }

    public int MaxParallel { get; set; }

    public event Action<JobItem>? JobAdded;
    public event Action<JobItem>? JobFinished;

    public IReadOnlyList<JobItem> Items
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    /// <summary>Queues a job. The queue takes ownership of <paramref name="password"/> (a copy is made).</summary>
    public JobItem Enqueue(JobRequest request, SecretPassword? password = null)
    {
        JobItem item;
        lock (_gate)
        {
            item = new JobItem(_nextId++, request, password?.Clone());
            _items.Add(item);
            _pending.Enqueue(item);
        }
        JobAdded?.Invoke(item);
        Pump();
        return item;
    }

    public void CancelAll()
    {
        foreach (var i in Items) i.Cancel();
    }

    public void ClearFinished()
    {
        lock (_gate) _items.RemoveAll(i => !i.IsActive);
    }

    private void Pump()
    {
        while (true)
        {
            JobItem? next = null;
            lock (_gate)
            {
                if (_running >= MaxParallel) return;
                while (_pending.Count > 0)
                {
                    var c = _pending.Dequeue();
                    if (c.IsActive) { next = c; break; }
                }
                if (next is null) return;
                _running++;
            }
            _ = RunAsync(next);
        }
    }

    private async Task RunAsync(JobItem item)
    {
        try
        {
            item.MarkStarted();
            var result = await Task.Run(() => _executor.ExecuteAsync(item.Request, item.Password, new SyncProgress(item), item.Token)).ConfigureAwait(false);
            item.Complete(result);
        }
        catch (Exception ex)
        {
            item.Complete(JobResult.Fail(ZiparkException.Wrap(ex)));
        }
        finally
        {
            lock (_gate) _running--;
            JobFinished?.Invoke(item);
            Pump();
        }
    }

    /// <summary>Progress sink that updates the item directly (WPF marshals scalar PropertyChanged to the UI thread).</summary>
    private sealed class SyncProgress(JobItem item) : IProgress<JobProgress>
    {
        public void Report(JobProgress p)
        {
            if (!item.IsActive) return;
            item.Progress = p;
            item.Stage = p.Stage switch
            {
                JobStage.Verifying or JobStage.Finalizing or JobStage.Scanning => p.Stage,
                _ => JobStage.Running,
            };
        }
    }

    public void Dispose()
    {
        CancelAll();
    }
}
