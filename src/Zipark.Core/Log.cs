using System.Text;

namespace Zipark.Core;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Minimal thread-safe file logger. Never pass passwords or file contents to it;
/// callers log paths and error codes only.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;
    private const long MaxBytes = 4 * 1024 * 1024;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public static string? Component { get; private set; }

    public static void Initialize(string component, string? directory = null)
    {
        Component = component;
        try
        {
            var dir = directory ?? AppPaths.LogDir;
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, $"{component}.log");
            Rotate();
        }
        catch
        {
            _file = null; // logging must never break the application
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel || _file is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_file, line, Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void Rotate()
    {
        if (_file is null || !File.Exists(_file)) return;
        if (new FileInfo(_file).Length < MaxBytes) return;
        var old = _file + ".1";
        if (File.Exists(old)) File.Delete(old);
        File.Move(_file, old);
    }
}
