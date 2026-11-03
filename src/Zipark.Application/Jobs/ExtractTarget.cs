using Zipark.Core;

namespace Zipark.Application.Jobs;

/// <summary>
/// Where "extract to a new folder" puts files. It never invents a new folder name: existing files are
/// handled per file by the conflict policy (e.g. the "file already exists" question), not by creating
/// "name (2)" folders.
/// </summary>
public static class ExtractTarget
{
    private static readonly string[] Suffixes =
        [".001", ".tar.gz", ".tar.xz", ".tar.bz2", ".7z", ".zip", ".rar", ".tar", ".gz", ".xz", ".bz2", ".tgz", ".txz", ".iso", ".cab", ".wim", ".zst"];

    /// <summary>"retro.zip" → "retro", "backup.7z.001" → "backup", "data.part1.rar" → "data".</summary>
    public static string FolderName(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        foreach (var suffix in Suffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        var partIdx = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (partIdx > 0) name = name[..partIdx];
        return PathSafety.SanitizeFileName(name);
    }

    /// <summary>
    /// The folder that will contain the extracted items for "extract to a new folder".
    /// If the archive already holds a single top-level folder with the archive's own name ("retro.zip" → "retro\…"),
    /// that folder is extracted as-is next to the archive (no "retro\retro"); otherwise items go into a folder
    /// named after the archive. Either folder is reused when it already exists.
    /// </summary>
    /// <returns>(Destination to pass to the engine, folder to show the user afterwards).</returns>
    public static (string Destination, string ResultFolder) ForNewFolder(ArchiveInfo info, string archivePath, string baseDir)
    {
        var folder = FolderName(archivePath);
        var top = SingleTopFolder(info);
        if (top is not null && string.Equals(top, folder, StringComparison.OrdinalIgnoreCase))
            return (baseDir, Path.Combine(baseDir, top));
        var dest = Path.Combine(baseDir, folder);
        return (dest, dest);
    }

    /// <summary>Name of the only top-level folder, or null when the archive has files at its root or several roots.</summary>
    public static string? SingleTopFolder(ArchiveInfo info)
    {
        string? top = null;
        bool isFolder = false;
        foreach (var e in info.Entries)
        {
            var parts = e.Path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (top is null) top = parts[0];
            else if (!string.Equals(top, parts[0], StringComparison.OrdinalIgnoreCase)) return null;
            if (parts.Length > 1 || e.IsDirectory) isFolder = true;
            else return null; // a file sits directly at the root
        }
        return isFolder ? top : null;
    }
}
