using Zipark.Core;

namespace Zipark.VirtualFileSystem;

public sealed class VfsNode
{
    public const uint AttrReadOnly = 0x1, AttrDirectory = 0x10, AttrArchive = 0x20;

    private List<VfsNode>? _children;
    private Dictionary<string, VfsNode>? _byName;

    public VfsNode(int id, string name, string path, bool isDirectory, VfsNode? parent)
    {
        Id = id;
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
        Parent = parent;
    }

    public int Id { get; }
    public string Name { get; }
    /// <summary>Windows-style path from the volume root, e.g. "\docs\a.txt". Root is "\".</summary>
    public string Path { get; }
    public bool IsDirectory { get; }
    public VfsNode? Parent { get; }
    public ArchiveEntry? Entry { get; internal set; }
    public long Size => Entry is { IsDirectory: false } e ? e.Size : 0;
    public ulong CreationTime { get; internal set; }
    public ulong LastWriteTime { get; internal set; }

    /// <summary>Files are reported read-only so Explorer and applications know the drive cannot be written.</summary>
    public uint Attributes => IsDirectory ? AttrDirectory : AttrReadOnly | AttrArchive;

    /// <summary>Children sorted by ordinal-ignore-case name (WinFsp directory markers depend on a stable order).</summary>
    public IReadOnlyList<VfsNode> Children => _children ?? (IReadOnlyList<VfsNode>)Array.Empty<VfsNode>();

    public VfsNode? Child(string name) => _byName is not null && _byName.TryGetValue(name, out var c) ? c : null;

    internal void AddChild(VfsNode child)
    {
        _children ??= [];
        _byName ??= new Dictionary<string, VfsNode>(StringComparer.OrdinalIgnoreCase);
        _children.Add(child);
        _byName[child.Name] = child;
    }

    internal void SortChildren()
    {
        _children?.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        if (_children is null) return;
        foreach (var c in _children) c.SortChildren();
    }

    /// <summary>Children whose names sort after <paramref name="marker"/> (exclusive), for paged enumeration.</summary>
    public IEnumerable<VfsNode> ChildrenAfter(string? marker)
    {
        var list = Children;
        int start = 0;
        if (!string.IsNullOrEmpty(marker))
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (StringComparer.OrdinalIgnoreCase.Compare(list[mid].Name, marker) <= 0) lo = mid + 1; else hi = mid;
            }
            start = lo;
        }
        for (int i = start; i < list.Count; i++) yield return list[i];
    }
}

/// <summary>Immutable directory index of an archive, built once from the engine listing.</summary>
public sealed class ArchiveIndex
{
    private readonly Dictionary<string, VfsNode> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, VfsNode> _byEntry = new();

    public VfsNode Root { get; }
    public ArchiveInfo Info { get; }
    public int NodeCount => _byPath.Count;
    public long TotalFileBytes { get; }

    public ArchiveIndex(ArchiveInfo info)
    {
        Info = info;
        var archiveTime = File.Exists(info.ArchivePath) ? (ulong)File.GetLastWriteTimeUtc(info.ArchivePath).ToFileTimeUtc() : (ulong)DateTime.UtcNow.ToFileTimeUtc();
        int nextId = 1;
        Root = new VfsNode(nextId++, "", "\\", true, null) { CreationTime = archiveTime, LastWriteTime = archiveTime };
        _byPath["\\"] = Root;
        long total = 0;

        foreach (var e in info.Entries)
        {
            if (PathSafety.Check(e.Path, out var norm) != PathSafety.Verdict.Safe) continue; // never expose unsafe names
            if (e.IsSymbolicLink) continue; // links inside archives are not followed on the virtual drive
            var parts = norm.Split('\\');
            var parent = Root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                var path = parent == Root ? "\\" + parts[i] : parent.Path + "\\" + parts[i];
                var existing = parent.Child(parts[i]);
                if (existing is null)
                {
                    existing = new VfsNode(nextId++, parts[i], path, !last || e.IsDirectory, parent)
                    {
                        CreationTime = archiveTime,
                        LastWriteTime = archiveTime,
                    };
                    parent.AddChild(existing);
                    _byPath[path] = existing;
                }
                else if (last && existing.IsDirectory != e.IsDirectory)
                {
                    break; // a file and a folder with the same name: keep the first one
                }
                if (last && existing.Entry is null)
                {
                    existing.Entry = e;
                    if (e.Modified is { } m) existing.LastWriteTime = (ulong)m.ToUniversalTime().ToFileTimeUtc();
                    existing.CreationTime = e.Created is { } c ? (ulong)c.ToUniversalTime().ToFileTimeUtc() : existing.LastWriteTime;
                    if (!e.IsDirectory)
                    {
                        _byEntry[e.Index] = existing;
                        total += e.Size;
                    }
                }
                parent = existing;
            }
        }
        Root.SortChildren();
        TotalFileBytes = total;
    }

    public VfsNode? Lookup(string path)
    {
        if (string.IsNullOrEmpty(path)) return Root;
        var p = path.Replace('/', '\\');
        if (!p.StartsWith('\\')) p = "\\" + p;
        if (p.Length > 1) p = p.TrimEnd('\\');
        return _byPath.TryGetValue(p, out var n) ? n : null;
    }

    public VfsNode? ByEntryIndex(int index) => _byEntry.TryGetValue(index, out var n) ? n : null;
}
