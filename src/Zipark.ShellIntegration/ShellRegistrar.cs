using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zipark.ShellIntegration;

/// <summary>Localized menu captions supplied by the UI layer.</summary>
public sealed record ShellLabels(
    string Root, string Open, string CompressZip, string Compress7z, string CompressAdvanced,
    string ExtractHere, string ExtractToFolder, string Test, string Mount, string Unmount);

public static class ShellVerbs
{
    public const string Open = "open";
    public const string CompressZip = "compress-zip";
    public const string Compress7z = "compress-7z";
    public const string CompressAdvanced = "compress-advanced";
    public const string ExtractHere = "extract-here";
    public const string ExtractToFolder = "extract-folder";
    public const string Test = "test";
    public const string Mount = "mount";
    public const string Unmount = "unmount";

    public static readonly string[] All = [Open, CompressZip, Compress7z, CompressAdvanced, ExtractHere, ExtractToFolder, Test, Mount, Unmount];
}

/// <summary>
/// Per-user Explorer integration (HKCU only, no administrator rights, fully reversible).
/// <list type="bullet">
/// <item>Windows 10: a cascading "Zipark" menu in the classic context menu.</item>
/// <item>Windows 11: the same entries appear under "Show more options" (Shift+F10). The new top-level
/// Windows 11 menu requires a packaged IExplorerCommand handler, which this release does not ship.</item>
/// </list>
/// Archive types are added to "Open with" (OpenWithProgids) — Zipark never takes over the default
/// handler of an extension without the user choosing it in Windows settings.
/// </summary>
public sealed class ShellRegistrar
{
    public const string ProgId = "Zipark.Archive";
    private const string MenuKey = "Zipark";

    public static readonly string[] ArchiveExtensions =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".txz", ".zst", ".iso", ".cab", ".wim", ".001"];

    public static readonly string[] MountableExtensions = [".zip", ".7z", ".iso"];

    private readonly RegistryKey _classes;

    /// <param name="classesRoot">Defaults to HKCU\Software\Classes; tests pass a scratch key.</param>
    public ShellRegistrar(RegistryKey? classesRoot = null)
    {
        _classes = classesRoot ?? Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true);
    }

    public static string Command(string exe, string verb, string arg = "%1") => $"\"{exe}\" --verb {verb} \"{arg}\"";

    public void Register(string exePath, ShellLabels l)
    {
        var icon = $"\"{exePath}\",0";

        // ProgID used for "Open with".
        using (var prog = _classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", "Zipark Archive");
            using (var di = prog.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", Command(exePath, ShellVerbs.Open));
        }

        // Any file / any folder: compression verbs.
        foreach (var target in new[] { "*", "Directory" })
        {
            CreateCascade($@"{target}\shell\{MenuKey}", l.Root, icon,
            [
                (ShellVerbs.CompressZip, l.CompressZip),
                (ShellVerbs.Compress7z, l.Compress7z),
                (ShellVerbs.CompressAdvanced, l.CompressAdvanced),
            ], exePath);
        }

        // Archive types: open / extract / test (+ mount for ZIP, 7Z, ISO).
        foreach (var ext in ArchiveExtensions)
        {
            var verbs = new List<(string, string)>
            {
                (ShellVerbs.Open, l.Open),
                (ShellVerbs.ExtractHere, l.ExtractHere),
                (ShellVerbs.ExtractToFolder, l.ExtractToFolder),
                (ShellVerbs.Test, l.Test),
            };
            if (MountableExtensions.Contains(ext)) verbs.Add((ShellVerbs.Mount, l.Mount));
            CreateCascade($@"SystemFileAssociations\{ext}\shell\{MenuKey}", l.Root, icon, verbs, exePath);
            using var owp = _classes.CreateSubKey($@"{ext}\OpenWithProgids");
            owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Drives: unmount a Zipark virtual drive.
        using (var drive = _classes.CreateSubKey($@"Drive\shell\{MenuKey}.Unmount"))
        {
            drive.SetValue("MUIVerb", l.Unmount);
            drive.SetValue("Icon", icon);
            using var cmd = drive.CreateSubKey("command");
            cmd.SetValue("", Command(exePath, ShellVerbs.Unmount));
        }
        NotifyShell();
    }

    private void CreateCascade(string path, string title, string icon, IEnumerable<(string Verb, string Label)> verbs, string exePath)
    {
        _classes.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        using var root = _classes.CreateSubKey(path);
        root.SetValue("MUIVerb", title);
        root.SetValue("Icon", icon);
        root.SetValue("SubCommands", "");
        int order = 0;
        foreach (var (verb, label) in verbs)
        {
            using var v = root.CreateSubKey($@"shell\{order++:D2}-{verb}");
            v.SetValue("MUIVerb", label);
            using var cmd = v.CreateSubKey("command");
            cmd.SetValue("", Command(exePath, verb));
        }
    }

    public void Unregister()
    {
        _classes.DeleteSubKeyTree(ProgId, false);
        _classes.DeleteSubKeyTree($@"*\shell\{MenuKey}", false);
        _classes.DeleteSubKeyTree($@"Directory\shell\{MenuKey}", false);
        _classes.DeleteSubKeyTree($@"Drive\shell\{MenuKey}.Unmount", false);
        foreach (var ext in ArchiveExtensions)
        {
            _classes.DeleteSubKeyTree($@"SystemFileAssociations\{ext}\shell\{MenuKey}", false);
            using var owp = _classes.OpenSubKey($@"{ext}\OpenWithProgids", writable: true);
            owp?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        NotifyShell();
    }

    public bool IsRegistered
    {
        get
        {
            using var k = _classes.OpenSubKey($@"*\shell\{MenuKey}");
            return k is not null;
        }
    }

    /// <summary>The executable the current registration points to, if any.</summary>
    public string? RegisteredExecutable
    {
        get
        {
            using var k = _classes.OpenSubKey($@"{ProgId}\shell\open\command");
            var cmd = k?.GetValue("") as string;
            if (cmd is null || !cmd.StartsWith('"')) return null;
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
    }

    private static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { /* best effort */ }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}

/// <summary>Parsed "--verb name path" command line from Explorer.</summary>
public sealed record ShellInvocation(string Verb, IReadOnlyList<string> Paths)
{
    public static ShellInvocation? Parse(IReadOnlyList<string> args)
    {
        string? verb = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--verb" && i + 1 < args.Count) verb = args[++i];
            else if (!args[i].StartsWith("--", StringComparison.Ordinal)) paths.Add(args[i]);
        }
        if (verb is null && paths.Count > 0) verb = ShellVerbs.Open;
        if (verb is null || !ShellVerbs.All.Contains(verb)) return null;
        return new ShellInvocation(verb, paths);
    }
}
