using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Zipark.Core;
using Zipark.Core.Native;
using Zipark.Crypto;

namespace Zipark.Compression;

public sealed record SevenZipRunResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode is 0 or 1;
}

/// <summary>
/// Runs 7z.exe in its own process. Arguments go through ArgumentList (no shell parsing),
/// passwords are written to stdin and never appear on the command line, and the process is
/// placed in a kill-on-close job so it cannot outlive Zipark.
/// </summary>
public static partial class SevenZipProcess
{
    // 7-Zip exit codes
    public const int ExitOk = 0, ExitWarning = 1, ExitFatal = 2, ExitCommandLine = 7, ExitMemory = 8, ExitUserBreak = 255;

    [GeneratedRegex(@"^\s*(\d{1,3})%\s*(?:\d+\s*)?(?:[+\-U=R.]\s+)?(.*)$")]
    private static partial Regex ProgressLine();

    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static Process Start(ProcessStartInfo psi, SecretPassword? password)
    {
        var p = Process.Start(psi) ?? throw new ZiparkException(ErrorCode.EngineCrashed, "Could not start 7z.exe");
        ProcessJob.Shared?.TryAdd(p);
        try
        {
            if (password is not null && !password.IsEmpty)
            {
                // Answer "Enter password" and, for creation, a possible verification prompt.
                password.WriteLineTo(p.StandardInput.BaseStream);
                password.WriteLineTo(p.StandardInput.BaseStream);
            }
            p.StandardInput.Close(); // any further prompt sees EOF instead of hanging
        }
        catch (IOException)
        {
            // process exited early; its exit code / stderr tell the story
        }
        return p;
    }

    /// <summary>Runs 7z to completion, parsing percent progress from stdout when <paramref name="onProgress"/> is set.</summary>
    public static async Task<SevenZipRunResult> RunAsync(
        string exe, IReadOnlyList<string> args, SecretPassword? password, string? workingDirectory,
        Action<double, string?>? onProgress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var p = Start(CreateStartInfo(exe, args, workingDirectory), password);
        using var reg = ct.Register(() => Kill(p));
        var stdoutTask = onProgress is null
            ? p.StandardOutput.ReadToEndAsync(CancellationToken.None)
            : ReadProgressAsync(p.StandardOutput, onProgress);
        var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new SevenZipRunResult(p.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadProgressAsync(StreamReader reader, Action<double, string?> onProgress)
    {
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buf = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buf, CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c is '\r' or '\n' or '\b')
                {
                    Flush();
                    if (c == '\n') all.Append('\n');
                }
                else
                {
                    line.Append(c);
                }
            }
        }
        Flush();
        return all.ToString();

        void Flush()
        {
            if (line.Length == 0) return;
            var s = line.ToString();
            line.Clear();
            var m = ProgressLine().Match(s);
            if (m.Success && double.TryParse(m.Groups[1].Value, out var pct))
            {
                var item = m.Groups[2].Value.Trim();
                onProgress(pct, item.Length > 0 ? item : null);
            }
            else if (!string.IsNullOrWhiteSpace(s))
            {
                all.Append(s.Trim()).Append('\n');
            }
        }
    }

    public static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
    }

    /// <summary>Maps 7-Zip's exit code and messages to an application error, or null on success.</summary>
    public static ZiparkException? Classify(SevenZipRunResult r, bool passwordSupplied)
    {
        if (r.ExitCode == ExitOk) return null;
        var text = r.StdErr + "\n" + r.StdOut;
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        // Without a password, stdin is closed: the engine's password prompt hits EOF and it reports a break.
        if (!passwordSupplied && Has("Enter password"))
            return new ZiparkException(ErrorCode.PasswordRequired, "The archive is encrypted. A password is required.");
        if (Has("Wrong password") || Has("Cannot open encrypted archive"))
            return passwordSupplied
                ? new ZiparkException(ErrorCode.WrongPassword, "Wrong password.")
                : new ZiparkException(ErrorCode.PasswordRequired, "The archive is encrypted. A password is required.");
        if (Has("Missing volume") || Has("Unexpected end of archive") && Has(".001"))
            return new ZiparkException(ErrorCode.MissingVolume, FirstError(text));
        if (Has("not enough space") || Has("There is not enough space on the disk"))
            return new ZiparkException(ErrorCode.DiskFull, FirstError(text));
        if (Has("Access is denied") || Has("access denied"))
            return new ZiparkException(ErrorCode.AccessDenied, FirstError(text));
        if (Has("Cannot open the file as archive") || Has("Cannot open the file as [") || Has("is not archive"))
            return new ZiparkException(ErrorCode.UnsupportedFormat, FirstError(text));
        if (Has("Data Error") || Has("CRC Failed") || Has("Headers Error") || Has("Unexpected end") || Has("Unsupported Method") || Has("There are data after the end"))
            return new ZiparkException(ErrorCode.CorruptArchive, FirstError(text));
        if (Has("The system cannot find the file") || Has("cannot find the file"))
            return new ZiparkException(ErrorCode.FileNotFound, FirstError(text));
        if (r.ExitCode == ExitWarning) return null;
        if (r.ExitCode == ExitMemory) return new ZiparkException(ErrorCode.EngineCrashed, "7-Zip ran out of memory.");
        if (r.ExitCode == ExitUserBreak) return new ZiparkException(ErrorCode.Cancelled, "Stopped.");
        if (r.ExitCode == ExitCommandLine) return new ZiparkException(ErrorCode.InvalidArgument, FirstError(text));
        if (r.ExitCode < 0 || r.ExitCode > 255) return new ZiparkException(ErrorCode.EngineCrashed, $"7-Zip terminated abnormally (0x{r.ExitCode:X8}).");
        return new ZiparkException(ErrorCode.CorruptArchive, FirstError(text));
    }

    private static string FirstError(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Enter password", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var err = lines.FirstOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault();
        return err ?? "7-Zip reported an error.";
    }
}
