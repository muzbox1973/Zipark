using Zipark.Cache;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.VirtualFileSystem;

public sealed record ContentStats(int Groups, int SolidGroups, long DecodeRuns, long BytesDecoded, long ManagedZipReads, CacheStats Cache);

/// <summary>
/// Lazy, random-access file content for the virtual drive.
/// <para>
/// Entries are organised in decode groups: one group per 7z solid block (folder), one group per entry for
/// ZIP and non-solid formats. Reading any byte of a file decodes only its group, streaming from the
/// engine straight into the chunk cache; other files of the same solid block are cached as a by-product,
/// so neighbours are served without decoding the block again. A run stops shortly after the last
/// requested file (read-ahead window) so a small file at the start of a huge block stays cheap.
/// Nothing is extracted to a temporary folder.
/// </para>
/// </summary>
public sealed class ArchiveContentProvider : IDisposable
{
    public const int DefaultChunkSize = 1 << 20;

    private sealed class DecodeGroup
    {
        public required int Id;
        public required List<ArchiveEntry> Entries;
        public readonly Dictionary<int, int> OrdinalOf = new();
        public readonly object Gate = new();
        public bool Running;
        public int ProducedOrdinal = -1;
        public int ProducedChunk = -1;
        public int MaxRequestedOrdinal = -1;
        public Exception? LastError;
        public readonly HashSet<ChunkKey> Wanted = new();
        public readonly Dictionary<ChunkKey, byte[]> Handoff = new();
        public bool IsSolid => Entries.Count > 1;
    }

    private readonly string _archivePath;
    private readonly IArchiveEngine _engine;
    private readonly SecretPassword? _password;
    private readonly HybridChunkCache _cache;
    private readonly int _chunkSize;
    private readonly long _readAheadBytes;
    private readonly Dictionary<int, DecodeGroup> _groupOfEntry = new();
    private readonly List<DecodeGroup> _groups = new();
    private readonly ManagedZipSource? _zip;
    private readonly SemaphoreSlim _decoderSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private long _decodeRuns, _bytesDecoded, _managedReads;

    public ArchiveContentProvider(string archivePath, ArchiveInfo info, IArchiveEngine engine, SecretPassword? password,
        HybridChunkCache cache, int chunkSize = DefaultChunkSize, long readAheadBytes = 64L * 1024 * 1024, int maxParallelDecoders = 4)
    {
        _archivePath = archivePath;
        _engine = engine;
        _password = password?.Clone();
        _cache = cache;
        _chunkSize = chunkSize;
        _readAheadBytes = readAheadBytes;
        _decoderSlots = new SemaphoreSlim(Math.Max(1, maxParallelDecoders));
        _zip = ManagedZipSource.TryCreate(archivePath, info);

        var files = info.Entries.Where(e => !e.IsDirectory && e.Size > 0).OrderBy(e => e.Index).ToList();
        if (info.Format == ArchiveFormat.SevenZip)
        {
            int nextId = 0;
            foreach (var block in files.GroupBy(e => e.Block ?? -1 - e.Index).OrderBy(g => g.Min(e => e.Index)))
                AddGroup(nextId++, block.ToList());
        }
        else
        {
            for (int i = 0; i < files.Count; i++) AddGroup(i, [files[i]]);
        }
    }

    private void AddGroup(int id, List<ArchiveEntry> entries)
    {
        var g = new DecodeGroup { Id = id, Entries = entries };
        for (int i = 0; i < entries.Count; i++)
        {
            g.OrdinalOf[entries[i].Index] = i;
            _groupOfEntry[entries[i].Index] = g;
        }
        _groups.Add(g);
    }

    public ContentStats Stats => new(_groups.Count, _groups.Count(g => g.IsSolid), Interlocked.Read(ref _decodeRuns),
        Interlocked.Read(ref _bytesDecoded), Interlocked.Read(ref _managedReads), _cache.Stats);

    /// <summary>Reads file bytes at <paramref name="offset"/>. Blocks until the needed chunks are decoded.</summary>
    public int Read(ArchiveEntry entry, long offset, Span<byte> destination, CancellationToken ct = default)
    {
        if (entry.IsDirectory || offset >= entry.Size || destination.Length == 0) return 0;
        int total = (int)Math.Min(destination.Length, entry.Size - offset);
        int copied = 0;
        while (copied < total)
        {
            long pos = offset + copied;
            int chunkIndex = (int)(pos / _chunkSize);
            int within = (int)(pos % _chunkSize);
            var chunk = GetChunk(entry, chunkIndex, ct);
            int n = Math.Min(chunk.Length - within, total - copied);
            if (n <= 0) throw new ZiparkException(ErrorCode.CorruptArchive, "Decoded data is shorter than the listed size.");
            chunk.AsSpan(within, n).CopyTo(destination[copied..]);
            copied += n;
        }
        return copied;
    }

    private byte[] GetChunk(ArchiveEntry entry, int chunkIndex, CancellationToken ct)
    {
        var key = new ChunkKey(entry.Index, chunkIndex);
        if (_cache.TryGet(key, out var hit)) return hit;
        if (!_groupOfEntry.TryGetValue(entry.Index, out var g))
            throw new ZiparkException(ErrorCode.FileNotFound, "Entry has no data.");
        int ordinal = g.OrdinalOf[entry.Index];

        lock (g.Gate)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_shutdown.IsCancellationRequested) throw new OperationCanceledException();
                if (g.Handoff.Remove(key, out var handed)) return handed;
                if (_cache.TryGet(key, out hit)) return hit;

                if (g.Running)
                {
                    bool passed = g.ProducedOrdinal > ordinal || g.ProducedOrdinal == ordinal && g.ProducedChunk >= chunkIndex;
                    if (!passed)
                    {
                        g.Wanted.Add(key);
                        g.MaxRequestedOrdinal = Math.Max(g.MaxRequestedOrdinal, ordinal);
                    }
                    Monitor.Wait(g.Gate, 500);
                    continue;
                }
                if (g.LastError is { } err)
                {
                    g.LastError = null; // next request retries
                    if (err is ZiparkException fx) throw new ZiparkException(fx.Code, fx.Message, fx);
                    throw new ZiparkException(ErrorCode.CorruptArchive, err.Message, err);
                }

                g.Running = true;
                g.Handoff.Clear();
                g.ProducedOrdinal = -1;
                g.ProducedChunk = -1;
                g.MaxRequestedOrdinal = ordinal;
                g.Wanted.Add(key);
                var group = g;
                // Dedicated thread: a decode run blocks on the engine pipe for its whole duration and must not
                // starve the thread pool (timers, cancellation and I/O completions depend on it).
                _ = Task.Factory.StartNew(() => DecodeRun(group), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }
    }

    private void DecodeRun(DecodeGroup g)
    {
        Stream? stream = null;
        try
        {
            _decoderSlots.Wait(_shutdown.Token);
            try
            {
                Interlocked.Increment(ref _decodeRuns);
                if (g.Entries.Count == 1 && _zip is not null && _zip.CanHandle(g.Entries[0]))
                {
                    stream = _zip.Open(g.Entries[0]);
                    Interlocked.Increment(ref _managedReads);
                }
                else
                {
                    stream = _engine.OpenEntries(_archivePath, g.Entries.Select(e => e.Path).ToList(), _password);
                }

                long bytesAfterLastRequested = 0;
                bool stoppedEarly = false;
                for (int ord = 0; ord < g.Entries.Count && !stoppedEarly; ord++)
                {
                    var e = g.Entries[ord];
                    long remaining = e.Size;
                    for (int ci = 0; remaining > 0; ci++)
                    {
                        _shutdown.Token.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(_chunkSize, remaining);
                        var buf = new byte[n];
                        stream.ReadExactly(buf);
                        remaining -= n;
                        Interlocked.Add(ref _bytesDecoded, n);
                        var key = new ChunkKey(e.Index, ci);
                        _cache.Put(key, buf);
                        lock (g.Gate)
                        {
                            if (g.Wanted.Remove(key)) g.Handoff[key] = buf;
                            g.ProducedOrdinal = ord;
                            g.ProducedChunk = ci;
                            Monitor.PulseAll(g.Gate);
                            if (ord > g.MaxRequestedOrdinal) bytesAfterLastRequested += n;
                            if (g.Wanted.Count == 0 && ord > g.MaxRequestedOrdinal && bytesAfterLastRequested >= _readAheadBytes)
                            {
                                stoppedEarly = true;
                                break;
                            }
                        }
                    }
                }
                if (!stoppedEarly)
                {
                    // Drain to the end so the engine's exit status (CRC / password) is verified.
                    Span<byte> probe = stackalloc byte[1];
                    if (stream.Read(probe) != 0) Log.Warn($"decode group {g.Id}: engine produced more data than listed");
                }
            }
            finally
            {
                _decoderSlots.Release();
            }
        }
        catch (Exception ex)
        {
            if (ex is EndOfStreamException) ex = new ZiparkException(ErrorCode.CorruptArchive, "Unexpected end of decoded data.", ex);
            if (ex is ZiparkException { Code: ErrorCode.WrongPassword or ErrorCode.CorruptArchive })
                foreach (var e in g.Entries) _cache.Invalidate(e.Index); // never serve data that failed verification
            if (ex is not OperationCanceledException) Log.Warn($"decode group {g.Id} failed: {ex.Message}");
            lock (g.Gate) g.LastError = ex;
        }
        finally
        {
            try { stream?.Dispose(); } catch { /* killing the engine */ }
            lock (g.Gate)
            {
                g.Running = false;
                g.Wanted.Clear();
                Monitor.PulseAll(g.Gate);
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var g in _groups)
            lock (g.Gate) Monitor.PulseAll(g.Gate);
        _zip?.Dispose();
        _password?.Dispose();
    }
}
