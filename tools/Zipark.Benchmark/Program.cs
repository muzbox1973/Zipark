using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zipark.Cache;
using Zipark.Compression;
using Zipark.Core;
using Zipark.VirtualFileSystem;

// Zipark.Benchmark — compares Zipark against the plain 7-Zip console on the same machine and data.
//   dotnet run -c Release --project tools/Zipark.Benchmark -- [--out docs/PERFORMANCE_REPORT.md] [--size-mb 256] [--runs 3]
// Nothing here claims Zipark is faster: it uses the same engine, so the interesting numbers are the
// wrapper overhead and the virtual-drive lazy read path.

string outFile = "PERFORMANCE_REPORT.md";
int sizeMb = 256, runs = 3;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--out") outFile = args[i + 1];
    if (args[i] == "--size-mb") sizeMb = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
    if (args[i] == "--runs") runs = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
}

var exe = SevenZipLocator.Find();
var engine = new SevenZipEngine(exe);
var work = Path.Combine(Path.GetTempPath(), "Zipark.Bench-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(work);
var data = Path.Combine(work, "data");
Console.WriteLine($"Generating {sizeMb} MB dataset in {data} …");
var (fileCount, totalBytes) = Dataset.Create(data, sizeMb * 1024L * 1024);

var rows = new List<string[]>();
try
{
    foreach (var (format, level) in new[] { (ArchiveFormat.SevenZip, CompressionLevel.Normal), (ArchiveFormat.Zip, CompressionLevel.Normal), (ArchiveFormat.SevenZip, CompressionLevel.Fastest) })
    {
        var ext = ArchiveFormats.Extension(format);
        var t7 = new List<double>(); var tf = new List<double>(); var x7 = new List<double>(); var xf = new List<double>();
        long size7 = 0, sizeF = 0, peak7 = 0;
        for (int r = 0; r < runs; r++)
        {
            var a7 = Path.Combine(work, $"plain-{format}-{level}-{r}{ext}");
            var af = Path.Combine(work, $"fx-{format}-{level}-{r}{ext}");
            // Alternate which tool goes first so warm-up / thermal effects do not favour one side.
            async Task RunPlain()
            {
                var (s, pk) = Plain.Run(exe, ["a", "-t" + ArchiveFormats.SevenZipType(format), $"-mx={(int)level}", "-mmt=on", "-bso0", "-bsp0", a7, data]);
                t7.Add(s); peak7 = Math.Max(peak7, pk); size7 = new FileInfo(a7).Length;
                await Task.CompletedTask;
            }
            async Task RunFx()
            {
                // Zipark engine: same 7z.exe plus safety checks, temp-file output and progress parsing.
                var w = Stopwatch.StartNew();
                await engine.CompressAsync(new CompressOptions { Sources = [data], OutputPath = af, Format = format, Level = level }, null);
                tf.Add(w.Elapsed.TotalSeconds); sizeF = new FileInfo(af).Length;
            }
            if (r % 2 == 0) { await RunPlain(); await RunFx(); } else { await RunFx(); await RunPlain(); }
            double sec;
            var sw = Stopwatch.StartNew();

            var d7 = Path.Combine(work, $"x7-{r}");
            (sec, _) = Plain.Run(exe, ["x", "-bso0", "-bsp0", "-y", "-o" + d7, a7]);
            x7.Add(sec);
            sw.Restart();
            await engine.ExtractAsync(new ExtractOptions { ArchivePath = af, Destination = Path.Combine(work, $"xf-{r}") }, null);
            xf.Add(sw.Elapsed.TotalSeconds);
            Directory.Delete(d7, true);
            Directory.Delete(Path.Combine(work, $"xf-{r}"), true);
            File.Delete(a7);
            if (r < runs - 1) File.Delete(af);
        }
        string label = $"{format} / {level}";
        rows.Add([label, "7-Zip console", Med(t7), Med(x7), Mb(size7), Pct(size7, totalBytes), Mb(peak7)]);
        rows.Add([label, "Zipark", Med(tf), Med(xf), Mb(sizeF), Pct(sizeF, totalBytes), "(same engine)"]);
        Console.WriteLine($"{label}: 7z {Med(t7)}s/{Med(x7)}s, Zipark {Med(tf)}s/{Med(xf)}s");
    }

    // Virtual drive read path (in-process model, no WinFsp needed): first byte of a single small file.
    var solid = Path.Combine(work, $"fx-{ArchiveFormat.SevenZip}-{CompressionLevel.Normal}-{runs - 1}.7z");
    var vfsRows = new List<string[]>();
    using (var fs = await ReadOnlyArchiveFileSystem.OpenAsync(solid, engine, null, new CacheOptions { DiskRoot = Path.Combine(work, "cache") }))
    {
        var small = fs.Index.Root.Children.SelectMany(Flatten).Where(n => !n.IsDirectory && n.Size > 0).OrderBy(n => n.Size).ElementAt(10);
        var buf = new byte[64 * 1024];
        var sw = Stopwatch.StartNew();
        fs.Read(small, 0, buf, out _);
        var first = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        fs.Read(small, 0, buf, out _);
        var cached = sw.Elapsed.TotalMilliseconds;
        var big = fs.Index.Root.Children.SelectMany(Flatten).Where(n => !n.IsDirectory).OrderByDescending(n => n.Size).First();
        sw.Restart();
        long off = 0; var big1 = new byte[1 << 20];
        while (off < big.Size) { fs.Read(big, off, big1, out var n); off += n; }
        var bigSec = sw.Elapsed.TotalSeconds;
        vfsRows.Add(["Open small file inside solid 7z (cold)", $"{first:F0} ms"]);
        vfsRows.Add(["Same file again (cache hit)", $"{cached:F2} ms"]);
        vfsRows.Add([$"Read largest file ({ByteSize.Format(big.Size)}), already decoded with its solid block", $"{bigSec:F2} s ({ByteSize.Format((long)(big.Size / Math.Max(bigSec, 1e-6)))}/s)"]);
        vfsRows.Add(["Engine decode runs / bytes decoded", $"{fs.Content.Stats.DecodeRuns} / {ByteSize.Format(fs.Content.Stats.BytesDecoded)}"]);
    }

    var sb = new StringBuilder();
    sb.AppendLine("# Zipark — Performance Report");
    sb.AppendLine();
    sb.AppendLine($"Measured {DateTime.Now:yyyy-MM-dd HH:mm} with `tools/Zipark.Benchmark` (median of {runs} runs).");
    sb.AppendLine();
    sb.AppendLine("## Environment");
    sb.AppendLine();
    sb.AppendLine($"- OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")}), {Environment.ProcessorCount} logical CPUs");
    sb.AppendLine($"- Engine: 7-Zip {FileVersionInfo.GetVersionInfo(exe).ProductVersion} (`{exe}`)");
    sb.AppendLine($"- .NET {Environment.Version}");
    sb.AppendLine($"- Dataset: {fileCount} files, {ByteSize.Format(totalBytes)} (≈40% text-like, ≈35% random, ≈25% repetitive binary)");
    sb.AppendLine();
    sb.AppendLine("## Compression / extraction");
    sb.AppendLine();
    sb.AppendLine("| Format / level | Tool | Compress (s) | Extract (s) | Archive size | Ratio | 7z peak RAM |");
    sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");
    foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r) + " |");
    sb.AppendLine();
    sb.AppendLine("Zipark drives the same 7-Zip engine, so compression ratio is identical by design. Differences in time are the");
    sb.AppendLine("wrapper's own work: listing + safety checks before extraction, extraction into a staging folder and moving files into");
    sb.AppendLine("place, temp-file output, and progress parsing. **Zipark is not claimed to be faster than 7-Zip.**");
    sb.AppendLine();
    sb.AppendLine("## Virtual drive read path (archive file-system core, without WinFsp)");
    sb.AppendLine();
    sb.AppendLine("| Scenario | Result |");
    sb.AppendLine("|---|---:|");
    foreach (var r in vfsRows) sb.AppendLine("| " + string.Join(" | ", r) + " |");
    sb.AppendLine();
    sb.AppendLine("The WinFsp kernel round-trip is not included (WinFsp was not installed on the measuring machine); see the release checklist.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine("report: " + Path.GetFullPath(outFile));
}
finally
{
    try { Directory.Delete(work, true); } catch { /* temp */ }
}

static IEnumerable<VfsNode> Flatten(VfsNode n) => n.IsDirectory ? n.Children.SelectMany(Flatten) : [n];
static string Med(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2].ToString("F2", CultureInfo.InvariantCulture); }
static string Mb(long b) => ByteSize.Format(b);
static string Pct(long a, long b) => (100.0 * a / b).ToString("F1", CultureInfo.InvariantCulture) + "%";

static class Plain
{
    public static (double Seconds, long PeakBytes) Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        long peak = 0;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        while (!p.WaitForExit(50))
        {
            try { p.Refresh(); peak = Math.Max(peak, p.PeakWorkingSet64); } catch { /* exited */ }
        }
        Task.WaitAll(outTask, errTask);
        if (p.ExitCode != 0) throw new Exception("7z failed: " + errTask.Result);
        return (sw.Elapsed.TotalSeconds, peak);
    }
}

static class Dataset
{
    public static (int Files, long Bytes) Create(string dir, long target)
    {
        Directory.CreateDirectory(dir);
        var rnd = new Random(20261102);
        var words = "archive compression block stream index cache volume drive header entry checksum password folder file data".Split(' ');
        long total = 0;
        int files = 0;
        while (total < target)
        {
            int kind = files % 20;
            var sub = Path.Combine(dir, $"set{files % 37:D2}");
            Directory.CreateDirectory(sub);
            byte[] bytes;
            if (kind < 8)
            {
                var sb = new StringBuilder();
                int len = rnd.Next(2_000, 400_000);
                while (sb.Length < len) sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(10) == 0 ? ".\n" : " ");
                bytes = Encoding.UTF8.GetBytes(sb.ToString());
            }
            else if (kind < 15)
            {
                bytes = RandomNumberGenerator.GetBytes(rnd.Next(1_000, 3_000_000));
            }
            else
            {
                bytes = new byte[rnd.Next(10_000, 2_000_000)];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i / 64) % 7 * 31 + rnd.Next(4));
            }
            File.WriteAllBytes(Path.Combine(sub, $"f{files:D5}.bin"), bytes);
            total += bytes.Length;
            files++;
        }
        return (files, total);
    }
}
