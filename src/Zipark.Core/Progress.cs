namespace Zipark.Core;

public enum JobStage { Queued, Preparing, Scanning, Running, Verifying, Finalizing, Completed, Failed, Cancelled }

/// <summary>Snapshot of a running operation. Immutable so it can cross threads and processes safely.</summary>
public sealed record JobProgress
{
    public JobStage Stage { get; init; } = JobStage.Running;
    public double Percent { get; init; }
    public long ProcessedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? Eta { get; init; }
    public string? CurrentItem { get; init; }
    public string? Message { get; init; }
}

/// <summary>Turns raw "percent done" samples into speed and ETA using an exponential moving average.</summary>
public sealed class ProgressTracker
{
    private readonly long _totalBytes;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private double _lastSeconds;
    private long _lastBytes;
    private double _emaSpeed;

    public ProgressTracker(long totalBytes) => _totalBytes = Math.Max(0, totalBytes);

    public TimeSpan Elapsed => _sw.Elapsed;

    public JobProgress FromPercent(double percent, string? item = null, JobStage stage = JobStage.Running)
    {
        percent = Math.Clamp(percent, 0, 100);
        var processed = (long)(_totalBytes * percent / 100.0);
        return FromBytes(processed, item, stage, percent);
    }

    public JobProgress FromBytes(long processed, string? item = null, JobStage stage = JobStage.Running, double? percent = null)
    {
        var now = _sw.Elapsed.TotalSeconds;
        var dt = now - _lastSeconds;
        if (dt >= 0.25)
        {
            var inst = (processed - _lastBytes) / dt;
            _emaSpeed = _emaSpeed <= 0 ? inst : _emaSpeed * 0.7 + inst * 0.3;
            _lastSeconds = now;
            _lastBytes = processed;
        }
        else if (_emaSpeed <= 0 && now > 0)
        {
            _emaSpeed = processed / now;
        }
        var pct = percent ?? (_totalBytes > 0 ? processed * 100.0 / _totalBytes : 0);
        TimeSpan? eta = null;
        if (_emaSpeed > 1 && _totalBytes > processed)
            eta = TimeSpan.FromSeconds(Math.Min((_totalBytes - processed) / _emaSpeed, TimeSpan.FromDays(30).TotalSeconds));
        return new JobProgress
        {
            Stage = stage,
            Percent = pct,
            ProcessedBytes = processed,
            TotalBytes = _totalBytes,
            BytesPerSecond = Math.Max(0, _emaSpeed),
            Eta = eta,
            CurrentItem = item,
        };
    }
}
