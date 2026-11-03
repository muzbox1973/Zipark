using System.IO;
using System.IO.Pipes;
using System.Text;
using Zipark.Core;

namespace Zipark.Desktop.Infrastructure;

/// <summary>
/// Keeps one Zipark window per user session. Explorer launches one process per selected file;
/// secondary processes forward their arguments over a named pipe and exit, and the primary instance
/// batches invocations that arrive close together (multi-select "Compress to ZIP" → one archive).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public bool IsPrimary { get; }
    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance(string id)
    {
        var session = Environment.UserName + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
        _pipeName = $"Zipark-{id}-{session}";
        _mutex = new Mutex(true, $@"Local\Zipark-{id}", out bool created);
        IsPrimary = created;
        if (IsPrimary) _ = ListenAsync();
    }

    public static bool Forward(string id, string[] args)
    {
        var session = Environment.UserName + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
        try
        {
            using var client = new NamedPipeClientStream(".", $"Zipark-{id}-{session}", PipeDirection.Out);
            client.Connect(3000);
            var payload = Encoding.UTF8.GetBytes(string.Join("\0", args));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var ms = new MemoryStream();
                await server.CopyToAsync(ms, _cts.Token);
                var args = Encoding.UTF8.GetString(ms.ToArray()).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("single-instance pipe: " + ex.Message);
                await Task.Delay(200);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        }
        _mutex.Dispose();
    }
}
