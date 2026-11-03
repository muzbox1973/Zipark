using System.Text;
using Zipark.Application.Jobs;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;

// Zipark.Worker — runs exactly one job, isolated from the UI process.
// stdin : line 1 = JobRequest JSON, line 2 = password (only when HasPassword), then optional "cancel".
// stdout: WorkerMessage JSON lines ("progress" …, then one "result").

Log.Initialize("worker");
Console.OutputEncoding = new UTF8Encoding(false);
var stdin = Console.OpenStandardInput();

var requestLine = ReadLine(stdin);
JobRequest? request = null;
try
{
    request = requestLine is null ? null : JobJson.DeserializeRequest(Encoding.UTF8.GetString(requestLine));
}
catch (System.Text.Json.JsonException)
{
}
if (request is null)
{
    Emit(new WorkerMessage { Type = "result", Result = new JobResult { Success = false, Error = ErrorCode.InvalidArgument, Message = "Invalid job request." } });
    return 2;
}

SecretPassword? password = null;
if (request.HasPassword)
{
    var raw = ReadLine(stdin) ?? [];
    var chars = new char[Encoding.UTF8.GetCharCount(raw)];
    Encoding.UTF8.GetChars(raw, chars);
    password = SecretPassword.FromChars(chars);
    Array.Clear(raw);
    Array.Clear(chars);
}

using var cts = new CancellationTokenSource();
TaskCompletionSource<ConflictAnswer>? pendingAnswer = null;
_ = Task.Run(() =>
{
    // "cancel", or the parent closing stdin, stops the job cleanly.
    // A JSON line is the user's answer to the conflict question currently waiting.
    var reason = "stdin closed";
    while (ReadLine(stdin) is { } l)
    {
        var text = Encoding.UTF8.GetString(l).Trim();
        if (text == "cancel") { reason = "cancel requested"; break; }
        if (text.StartsWith('{'))
        {
            ConflictAnswer? answer = null;
            try { answer = JobJson.DeserializeAnswer(text); } catch (System.Text.Json.JsonException) { }
            if (answer is not null) Interlocked.Exchange(ref pendingAnswer, null)?.TrySetResult(answer);
        }
    }
    Log.Info($"worker {Environment.ProcessId}: stopping job ({reason})");
    cts.Cancel();
});

// Called on the extraction thread for each name conflict (ConflictPolicy.Ask): ask the UI and wait.
ConflictDecision AskUi(ConflictInfo info)
{
    var tcs = new TaskCompletionSource<ConflictAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
    Volatile.Write(ref pendingAnswer, tcs);
    Emit(new WorkerMessage { Type = "conflict", Conflict = info });
    tcs.Task.Wait(cts.Token); // OperationCanceledException when the job is cancelled meanwhile
    return new ConflictDecision(tcs.Task.Result.Choice, tcs.Task.Result.ApplyToAll);
}

JobResult result;
try
{
    var engine = new SevenZipEngine();
    var last = DateTime.MinValue;
    var progress = new SyncProgress(p =>
    {
        var now = DateTime.UtcNow;
        if ((now - last).TotalMilliseconds < 150 && p.Percent < 100) return;
        last = now;
        Emit(new WorkerMessage { Type = "progress", Progress = p });
    });
    result = await new JobRunner(engine, AskUi).RunAsync(request, password, progress, cts.Token);
}
catch (ZiparkException ex)
{
    result = JobResult.Fail(ex);
}
finally
{
    password?.Dispose();
}
Emit(new WorkerMessage { Type = "result", Result = result });
return result.Success ? 0 : 1;

static void Emit(WorkerMessage m)
{
    lock (typeof(SyncProgress))
    {
        Console.Out.WriteLine(JobJson.Serialize(m));
        Console.Out.Flush();
    }
}

static byte[]? ReadLine(Stream s)
{
    var buf = new List<byte>(256);
    int b;
    while ((b = s.ReadByte()) >= 0)
    {
        if (b == '\n') break;
        buf.Add((byte)b);
    }
    if (b < 0 && buf.Count == 0) return null;
    if (buf.Count > 0 && buf[^1] == '\r') buf.RemoveAt(buf.Count - 1);
    // Some callers (e.g. Windows PowerShell) prefix the stream with a UTF-8 BOM.
    if (buf.Count >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) buf.RemoveRange(0, 3);
    return buf.ToArray();
}

sealed class SyncProgress(Action<JobProgress> report) : IProgress<JobProgress>
{
    public void Report(JobProgress value) => report(value);
}
