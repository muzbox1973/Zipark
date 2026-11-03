using System.Collections.Concurrent;
using System.IO.Compression;
using Zipark.Core;

namespace Zipark.VirtualFileSystem;

/// <summary>
/// Fast path for unencrypted Deflate/Store ZIP entries using System.IO.Compression: no engine process
/// per file, true per-entry random access. Entries it cannot map (legacy code-page names, AES, other
/// methods) fall back to the 7-Zip engine.
/// </summary>
internal sealed class ManagedZipSource : IDisposable
{
    private readonly string _path;
    private readonly ConcurrentBag<Instance> _pool = new();
    private readonly HashSet<string> _names;
    private bool _disposed;

    private sealed class Instance(FileStream file, ZipArchive zip, Dictionary<string, ZipArchiveEntry> map) : IDisposable
    {
        public ZipArchive Zip { get; } = zip;
        public Dictionary<string, ZipArchiveEntry> Map { get; } = map;

        public void Dispose()
        {
            Zip.Dispose();
            file.Dispose();
        }
    }

    private ManagedZipSource(string path, HashSet<string> names, Instance first)
    {
        _path = path;
        _names = names;
        _pool.Add(first);
    }

    public static ManagedZipSource? TryCreate(string path, ArchiveInfo info)
    {
        if (info.Format != ArchiveFormat.Zip || info.VolumeCount > 1) return null;
        try
        {
            var inst = CreateInstance(path);
            var names = new HashSet<string>(inst.Map.Keys, StringComparer.Ordinal);
            return new ManagedZipSource(path, names, inst);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            Log.Info("managed zip fast path unavailable: " + ex.Message);
            return null;
        }
    }

    private static Instance CreateInstance(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var e in zip.Entries) map.TryAdd(e.FullName.Replace('/', '\\'), e);
            return new Instance(fs, zip, map);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public bool CanHandle(ArchiveEntry e)
    {
        if (e.Encrypted || e.IsDirectory) return false;
        var m = e.Method ?? "";
        bool supported = m.Length == 0 || m.StartsWith("Store", StringComparison.OrdinalIgnoreCase) || m.StartsWith("Deflate", StringComparison.OrdinalIgnoreCase);
        return supported && _names.Contains(e.Path);
    }

    public Stream Open(ArchiveEntry e)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pool.TryTake(out var inst)) inst = CreateInstance(_path);
        try
        {
            var entry = inst.Map[e.Path];
            if (entry.Length != e.Size) throw new InvalidDataException("ZIP entry size mismatch.");
            return new ReturningStream(entry.Open(), () => Return(inst));
        }
        catch
        {
            Return(inst);
            throw;
        }
    }

    private void Return(Instance inst)
    {
        if (_disposed || _pool.Count >= 4) inst.Dispose();
        else _pool.Add(inst);
    }

    public void Dispose()
    {
        _disposed = true;
        while (_pool.TryTake(out var i)) i.Dispose();
    }

    private sealed class ReturningStream(Stream inner, Action onDispose) : Stream
    {
        private bool _done;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done)
            {
                _done = true;
                inner.Dispose();
                onDispose();
            }
            base.Dispose(disposing);
        }
    }
}
