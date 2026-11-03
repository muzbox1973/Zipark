using System.Buffers.Binary;
using System.Diagnostics;
using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.Cache;

public readonly record struct ChunkKey(int EntryId, int ChunkIndex);

public sealed record CacheOptions
{
    public long MemoryLimitBytes { get; init; } = 256L * 1024 * 1024;
    public long DiskLimitBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public bool EnableDisk { get; init; } = true;
    /// <summary>Always on by default: spilled chunks are AES-256-GCM sealed with a per-session in-memory key.</summary>
    public bool EncryptDisk { get; init; } = true;
    public string? DiskRoot { get; init; }
}

public sealed record CacheStats(long MemoryBytes, long DiskBytes, int MemoryChunks, int DiskChunks, long Hits, long Misses, long Evictions);

/// <summary>
/// Two-tier LRU chunk cache: RAM first, then (optionally) disk. RAM evictions spill to disk; disk
/// evictions delete the file. Size limits are hard caps. Disk files are encrypted with
/// <see cref="ChunkCipher"/> so decrypted archive content is never stored on disk in plaintext,
/// and the whole session folder is removed on dispose.
/// </summary>
public sealed class HybridChunkCache : IDisposable
{
    private sealed class Item
    {
        public required ChunkKey Key;
        public byte[]? Data;     // in memory
        public long DiskLength;  // > 0 when stored on disk
    }

    private readonly CacheOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<ChunkKey, LinkedListNode<Item>> _mem = new();
    private readonly LinkedList<Item> _memLru = new();
    private readonly Dictionary<ChunkKey, LinkedListNode<Item>> _disk = new();
    private readonly LinkedList<Item> _diskLru = new();
    private readonly ChunkCipher? _cipher;
    private readonly string? _diskDir;
    private long _memBytes, _diskBytes, _hits, _misses, _evictions;
    private bool _disposed;

    public HybridChunkCache(CacheOptions? options = null)
    {
        _options = options ?? new CacheOptions();
        if (_options.EnableDisk && _options.DiskLimitBytes > 0)
        {
            var root = _options.DiskRoot ?? AppPaths.CacheRoot;
            _diskDir = Path.Combine(root, $"session-{Environment.ProcessId}-{Guid.NewGuid():N}");
            var di = Directory.CreateDirectory(_diskDir);
            di.Attributes |= FileAttributes.NotContentIndexed;
            if (_options.EncryptDisk) _cipher = new ChunkCipher();
        }
    }

    public CacheOptions Options => _options;
    public string? DiskDirectory => _diskDir;

    public CacheStats Stats
    {
        get
        {
            lock (_gate) return new CacheStats(_memBytes, _diskBytes, _mem.Count, _disk.Count, _hits, _misses, _evictions);
        }
    }

    public bool Contains(ChunkKey key)
    {
        lock (_gate) return _mem.ContainsKey(key) || _disk.ContainsKey(key);
    }

    public bool TryGet(ChunkKey key, out byte[] data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mem.TryGetValue(key, out var node))
            {
                _memLru.Remove(node);
                _memLru.AddFirst(node);
                _hits++;
                data = node.Value.Data!;
                return true;
            }
            if (_disk.TryGetValue(key, out var dnode))
            {
                var loaded = ReadFromDisk(dnode.Value);
                if (loaded is not null)
                {
                    _hits++;
                    // Promote back to RAM; the disk copy stays as a backing copy until evicted.
                    _diskLru.Remove(dnode);
                    _diskLru.AddFirst(dnode);
                    AddToMemory(key, loaded, spill: false);
                    data = loaded;
                    return true;
                }
                RemoveDisk(dnode);
            }
            _misses++;
            data = [];
            return false;
        }
    }

    public void Put(ChunkKey key, byte[] data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mem.TryGetValue(key, out var existing))
            {
                _memBytes -= existing.Value.Data!.Length;
                _memLru.Remove(existing);
                _mem.Remove(key);
            }
            AddToMemory(key, data, spill: true);
        }
    }

    /// <summary>Drops every chunk belonging to an entry (e.g. after a decode error).</summary>
    public void Invalidate(int entryId)
    {
        lock (_gate)
        {
            foreach (var k in _mem.Keys.Where(k => k.EntryId == entryId).ToList())
            {
                var n = _mem[k];
                _memBytes -= n.Value.Data!.Length;
                _memLru.Remove(n);
                _mem.Remove(k);
            }
            foreach (var k in _disk.Keys.Where(k => k.EntryId == entryId).ToList()) RemoveDisk(_disk[k]);
        }
    }

    private void AddToMemory(ChunkKey key, byte[] data, bool spill)
    {
        if (data.Length > _options.MemoryLimitBytes)
        {
            // Too large for RAM: go straight to disk if possible.
            if (spill) WriteToDisk(key, data);
            return;
        }
        var node = _memLru.AddFirst(new Item { Key = key, Data = data });
        _mem[key] = node;
        _memBytes += data.Length;
        while (_memBytes > _options.MemoryLimitBytes && _memLru.Last is { } last && last != node)
        {
            _memLru.RemoveLast();
            _mem.Remove(last.Value.Key);
            _memBytes -= last.Value.Data!.Length;
            _evictions++;
            if (!_disk.ContainsKey(last.Value.Key)) WriteToDisk(last.Value.Key, last.Value.Data!);
        }
    }

    private string DiskPath(ChunkKey key) => Path.Combine(_diskDir!, $"{key.EntryId:x8}_{key.ChunkIndex:x8}.chk");

    private static byte[] Aad(ChunkKey key)
    {
        var aad = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(aad, key.EntryId);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(4), key.ChunkIndex);
        return aad;
    }

    private void WriteToDisk(ChunkKey key, byte[] data)
    {
        if (_diskDir is null || data.Length > _options.DiskLimitBytes) return;
        try
        {
            var payload = _cipher is not null ? _cipher.Seal(data, Aad(key)) : data;
            while (_diskBytes + payload.Length > _options.DiskLimitBytes && _diskLru.Last is { } last)
            {
                RemoveDisk(last);
                _evictions++;
            }
            File.WriteAllBytes(DiskPath(key), payload);
            var node = _diskLru.AddFirst(new Item { Key = key, DiskLength = payload.Length });
            _disk[key] = node;
            _diskBytes += payload.Length;
        }
        catch (IOException ex)
        {
            // Disk full or locked: the chunk is simply not cached; it will be decoded again on demand.
            Log.Warn("cache spill failed: " + ex.Message);
        }
    }

    private byte[]? ReadFromDisk(Item item)
    {
        try
        {
            var raw = File.ReadAllBytes(DiskPath(item.Key));
            if (_cipher is null) return raw;
            var plain = new byte[raw.Length - ChunkCipher.Overhead];
            _cipher.Open(raw, Aad(item.Key), plain);
            return plain;
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
        {
            Log.Warn("cache read failed: " + ex.Message);
            return null;
        }
    }

    private void RemoveDisk(LinkedListNode<Item> node)
    {
        _diskLru.Remove(node);
        _disk.Remove(node.Value.Key);
        _diskBytes -= node.Value.DiskLength;
        try { File.Delete(DiskPath(node.Value.Key)); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _mem.Clear();
            _memLru.Clear();
            _disk.Clear();
            _diskLru.Clear();
            _cipher?.Dispose();
            if (_diskDir is not null)
            {
                try { Directory.Delete(_diskDir, true); } catch { /* removed at next start */ }
            }
        }
    }

    /// <summary>Deletes cache folders left by processes that no longer run (e.g. after a crash).</summary>
    public static int CleanupStaleSessions(string? root = null)
    {
        int removed = 0;
        var dir = root ?? AppPaths.CacheRoot;
        if (!Directory.Exists(dir)) return 0;
        foreach (var d in Directory.GetDirectories(dir, "session-*"))
        {
            var parts = Path.GetFileName(d).Split('-');
            if (parts.Length < 3 || !int.TryParse(parts[1], out var pid)) continue;
            bool alive;
            try
            {
                using var p = Process.GetProcessById(pid);
                alive = !p.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false;
            }
            if (alive) continue;
            try
            {
                Directory.Delete(d, true);
                removed++;
            }
            catch
            {
                // in use
            }
        }
        return removed;
    }
}
