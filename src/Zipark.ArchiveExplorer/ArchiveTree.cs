using Zipark.Core;

namespace Zipark.ArchiveExplorer;

/// <summary>Folder/file node of an archive listing. Folders implied by file paths are synthesized.</summary>
public sealed class ArchiveNode
{
    private readonly Dictionary<string, ArchiveNode> _children = new(StringComparer.OrdinalIgnoreCase);
    private List<ArchiveNode>? _sorted;

    internal ArchiveNode(string name, string fullPath, bool isDirectory, ArchiveNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;
    }

    public string Name { get; }
    /// <summary>Path inside the archive ('\' separated, no leading separator). Root = "".</summary>
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public ArchiveNode? Parent { get; }
    public ArchiveEntry? Entry { get; internal set; }
    public long Size { get; internal set; }
    public long PackedSize { get; internal set; }
    public DateTime? Modified => Entry?.Modified;
    public bool Encrypted => Entry?.Encrypted ?? false;
    public int FileCount { get; internal set; }

    /// <summary>Children sorted folders-first, then by name (cached).</summary>
    public IReadOnlyList<ArchiveNode> Children => _sorted ??= _children.Values
        .OrderByDescending(c => c.IsDirectory)
        .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public bool TryGetChild(string name, out ArchiveNode child) => _children.TryGetValue(name, out child!);

    internal ArchiveNode GetOrAdd(string name, bool isDirectory)
    {
        if (_children.TryGetValue(name, out var existing)) return existing;
        var path = FullPath.Length == 0 ? name : FullPath + "\\" + name;
        var node = new ArchiveNode(name, path, isDirectory, this);
        _children[name] = node;
        _sorted = null;
        return node;
    }

    public IEnumerable<ArchiveNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            if (c.IsDirectory)
                foreach (var d in c.Descendants()) yield return d;
        }
    }

    public override string ToString() => FullPath;
}

public sealed class ArchiveTree
{
    public ArchiveNode Root { get; }
    public ArchiveInfo Info { get; }

    private ArchiveTree(ArchiveInfo info, ArchiveNode root)
    {
        Info = info;
        Root = root;
    }

    public static ArchiveTree Build(ArchiveInfo info)
    {
        var root = new ArchiveNode("", "", true, null);
        foreach (var e in info.Entries)
        {
            var parts = e.Path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var node = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                node = node.GetOrAdd(parts[i], !last || e.IsDirectory);
            }
            node.Entry = e;
        }
        Aggregate(root, info.IsSolid);
        return new ArchiveTree(info, root);
    }

    /// <summary>Sums sizes per folder. Packed size is -1 ("unknown") for solid archives, where it belongs to a block, not a file.</summary>
    private static void Aggregate(ArchiveNode node, bool solid)
    {
        if (!node.IsDirectory)
        {
            node.Size = node.Entry?.Size ?? 0;
            node.PackedSize = solid ? -1 : node.Entry?.PackedSize ?? 0;
            node.FileCount = 1;
            return;
        }
        long size = 0, packed = 0;
        int count = 0;
        foreach (var c in node.Children)
        {
            Aggregate(c, solid);
            size += c.Size;
            packed += Math.Max(0, c.PackedSize);
            count += c.FileCount;
        }
        node.Size = size;
        node.PackedSize = solid ? -1 : packed;
        node.FileCount = count;
    }

    public ArchiveNode? Find(string path)
    {
        var node = Root;
        foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.TryGetChild(part, out var child)) return null;
            node = child;
        }
        return node;
    }

    /// <summary>Case-insensitive substring / wildcard (*, ?) search over names.</summary>
    public IEnumerable<ArchiveNode> Search(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return [];
        bool wildcard = pattern.Contains('*') || pattern.Contains('?');
        return Root.Descendants().Where(n => wildcard
            ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, n.Name, ignoreCase: true)
            : n.Name.Contains(pattern, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Expands selected nodes to archive paths suitable for <c>ExtractOptions.Selection</c>.</summary>
    public static IReadOnlyCollection<string> ToSelection(IEnumerable<ArchiveNode> nodes) =>
        nodes.Select(n => n.FullPath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Breadcrumb from root to the node.</summary>
    public static IReadOnlyList<ArchiveNode> PathTo(ArchiveNode node)
    {
        var list = new List<ArchiveNode>();
        for (var n = node; n is not null; n = n.Parent) list.Add(n);
        list.Reverse();
        return list;
    }
}
