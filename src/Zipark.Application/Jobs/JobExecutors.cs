using System.Diagnostics;
using System.Text;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Core.Native;
using Zipark.Crypto;

namespace Zipark.Application.Jobs;

public interface IJobExecutor
{
    Task<JobResult> ExecuteAsync(JobRequest request, SecretPassword? password, IProgress<JobProgress>? progress, CancellationToken ct);
}

public sealed class InProcessJobExecutor(IArchiveEngine engine) : IJobExecutor
{
    public Task<JobResult> ExecuteAsync(JobRequest request, SecretPassword? password, IProgress<JobProgress>? progress, CancellationToken ct) =>
        new JobRunner(engine).RunAsync(request, password, progress, ct);
}

/// <summary>
/// Runs each job in a separate Zipark.Worker process. A crash or hang in the worker (or the native
/// engine below it) surfaces as a failed job; the UI keeps running. Cancellation first asks the worker
/// to stop cleanly (it removes partial output), then kills it after a grace period.
/// </summary>
public sealed class WorkerProcessJobExecutor : IJobExecutor
{
    private readonly string _workerExe;

    public WorkerProcessJobExecutor(string? workerExe = null)
    {
        _workerExe = workerExe ?? Path.Combine(AppContext.BaseDirectory, "Zipark.Worker.exe");
    }

    public static bool IsAvailable(string? workerExe = null) =>
        File.Exists(workerExe ?? Path.Combine(AppContext.BaseDirectory, "Zipark.Worker.exe"));

    public async Task<JobResult> ExecuteAsync(JobRequest request, SecretPassword? password, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        request.HasPassword = password is { IsEmpty: false };
        var psi = new ProcessStartInfo(_workerExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(Compression.SevenZipLocator.OverridePath))
            psi.Environment["FREEARCX_7Z"] = Compression.SevenZipLocator.OverridePath;

        using var p = Process.Start(psi) ?? throw new ZiparkException(ErrorCode.EngineCrashed, "Worker did not start.");
        ProcessJob.Shared?.TryAdd(p);
        var stdin = p.StandardInput.BaseStream;
        var line = Encoding.UTF8.GetBytes(JobJson.Serialize(request) + "\n");
        await stdin.WriteAsync(line, CancellationToken.None).ConfigureAwait(false);
        if (request.HasPassword) password!.WriteLineTo(stdin);
        await stdin.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        using var reg = ct.Register(() =>
        {
            try
            {
                stdin.Write("cancel\n"u8);
                stdin.Flush();
            }
            catch
            {
                // worker already gone
            }
            _ = Task.Delay(5000).ContinueWith(_ => SevenZipProcess.Kill(p), TaskScheduler.Default);
        });

        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        JobResult? result = null;
        while (await p.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } l)
        {
            WorkerMessage? msg;
            try
            {
                msg = JobJson.DeserializeMessage(l);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (msg?.Type == "progress" && msg.Progress is not null) progress?.Report(msg.Progress);
            else if (msg?.Type == "result") result = msg.Result;
        }
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        try { stdin.Close(); } catch { /* closed */ }

        if (result is not null) return result;
        var err = await stderr.ConfigureAwait(false);
        if (ct.IsCancellationRequested) return new JobResult { Success = false, Error = ErrorCode.Cancelled, Message = "Cancelled." };
        Log.Error($"worker exited with {p.ExitCode} without result: {err}");
        return new JobResult { Success = false, Error = ErrorCode.EngineCrashed, Message = $"Worker process ended unexpectedly (exit {p.ExitCode})." };
    }
}
