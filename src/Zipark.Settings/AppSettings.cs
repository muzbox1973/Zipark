using System.Text.Json;
using System.Text.Json.Serialization;
using Zipark.Core;

namespace Zipark.Settings;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// User preferences. Passwords are never part of the settings (by design there is no field for them).
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    /// <summary>"ko" or "en".</summary>
    public string Language { get; set; } = "ko";
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>Beginner mode hides expert options.</summary>
    public bool SimpleMode { get; set; } = true;

    public ArchiveFormat DefaultFormat { get; set; } = ArchiveFormat.Zip;
    public int DefaultLevel { get; set; } = 5;
    /// <summary>0 = all cores.</summary>
    public int Threads { get; set; }
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;
    public bool OpenFolderAfterExtract { get; set; } = true;
    public bool VerifyAfterCompress { get; set; }
    public int MaxParallelJobs { get; set; } = 1;
    public bool UseWorkerProcess { get; set; } = true;

    public int MemoryCacheMb { get; set; } = 256;
    public int DiskCacheMb { get; set; } = 2048;
    /// <summary>Preferred first drive letter for virtual drives (searched downwards).</summary>
    public char PreferredDriveLetter { get; set; } = 'X';

    public long BombMaxTotalGb { get; set; } = 256;
    public int BombSuspiciousRatio { get; set; } = 1000;

    /// <summary>Optional explicit 7z.exe path (empty = auto-detect).</summary>
    public string SevenZipPath { get; set; } = "";

    public List<string> RecentArchives { get; set; } = [];

    public BombLimits ToBombLimits() => new()
    {
        MaxTotalBytes = Math.Max(1, BombMaxTotalGb) * 1024L * 1024 * 1024,
        SuspiciousRatio = Math.Max(10, BombSuspiciousRatio),
    };

    public void AddRecent(string path)
    {
        RecentArchives.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentArchives.Insert(0, path);
        if (RecentArchives.Count > 15) RecentArchives.RemoveRange(15, RecentArchives.Count - 15);
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.RoamingDir, "settings.json");
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("settings unreadable, using defaults: " + ex.Message);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { /* keep going */ }
        }
        var s = new AppSettings();
        if (!System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase))
            s.Language = "en";
        return s;
    }

    /// <summary>Atomic save: write a temp file, then replace.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tmp, _path, overwrite: true);
    }
}
