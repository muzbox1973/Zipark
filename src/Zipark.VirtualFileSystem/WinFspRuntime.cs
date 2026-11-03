using Microsoft.Win32;

namespace Zipark.VirtualFileSystem;

/// <summary>
/// Detects the WinFsp runtime (a kernel driver installed separately by the user). Zipark never
/// installs it silently; the UI explains why it is needed and links to the official installer.
/// </summary>
public static class WinFspRuntime
{
    public const string DownloadUrl = "https://winfsp.dev/rel/";

    public static string? InstallDirectory
    {
        get
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hklm.OpenSubKey(@"SOFTWARE\WinFsp");
                    if (key?.GetValue("InstallDir") is string dir && Directory.Exists(dir)) return dir;
                }
                catch
                {
                    // ignore and try the other view
                }
            }
            return null;
        }
    }

    public static bool IsInstalled
    {
        get
        {
            var dir = InstallDirectory;
            return dir is not null && File.Exists(Path.Combine(dir, "bin", Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll"));
        }
    }

    public static Version? InstalledVersion
    {
        get
        {
            var dir = InstallDirectory;
            if (dir is null) return null;
            var dll = Path.Combine(dir, "bin", "winfsp-x64.dll");
            if (!File.Exists(dll)) return null;
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
    }
}
