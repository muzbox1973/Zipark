using System.Diagnostics;
using Zipark.Core;

namespace Zipark.Compression;

/// <summary>
/// Read-only stream over 7z.exe's stdout (<c>x -so</c>). Reading to the end verifies the engine's exit
/// status, so a wrong password or corrupt data surfaces as an exception instead of silently short data.
/// Disposing early kills the engine process.
/// </summary>
public sealed class EntryDataStream : Stream
{
    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly Task<string> _stderr;
    private readonly string? _listFile;
    private readonly bool _passwordSupplied;
    private long _position;
    private bool _completed;

    internal EntryDataStream(Process process, string? listFile, bool passwordSupplied)
    {
        _process = process;
        _stdout = process.StandardOutput.BaseStream;
        _stderr = process.StandardError.ReadToEndAsync();
        _listFile = listFile;
        _passwordSupplied = passwordSupplied;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = _stdout.Read(buffer);
        _position += n;
        if (n == 0 && buffer.Length > 0) Complete();
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await _stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += n;
        if (n == 0 && buffer.Length > 0) Complete();
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        _process.WaitForExit();
        var result = new SevenZipRunResult(_process.ExitCode, "", _stderr.GetAwaiter().GetResult());
        var error = SevenZipProcess.Classify(result, _passwordSupplied);
        if (error is not null) throw error;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SevenZipProcess.Kill(_process);
            _process.Dispose();
            if (_listFile is not null)
            {
                try { File.Delete(_listFile); } catch { /* temp file */ }
            }
        }
        base.Dispose(disposing);
    }
}
