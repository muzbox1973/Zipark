using Zipark.Core;
using Microsoft.Win32;

namespace Zipark.Compression;

/// <summary>
/// Finds the 7-Zip console engine (7z.exe + 7z.dll). Search order:
/// explicit override → bundled "engine" folder next to Zipark → 7-Zip registry key → Program Files.
/// Zipark never downloads the engine itself.
/// </summary>
public static class SevenZipLocator
{
    public static string? OverridePath { get; set; }

    public static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(OverridePath)) yield return OverridePath!;
        var env = Environment.GetEnvironmentVariable("FREEARCX_7Z");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, "engine", "7z.exe");
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? path = null;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\7-Zip");
                path = key?.GetValue("Path64") as string ?? key?.GetValue("Path") as string;
            }
            catch
            {
                // registry unavailable: keep searching
            }
            if (path is not null) yield return Path.Combine(path, "7z.exe");
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe");
    }

    public static string? TryFind() =>
        Candidates().FirstOrDefault(p => File.Exists(p) && File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "7z.dll")));

    public static string Find() => TryFind() ?? throw new ZiparkException(ErrorCode.EngineNotFound,
        "7-Zip engine (7z.exe/7z.dll) was not found. Install 7-Zip or place it in the 'engine' folder.");
}
