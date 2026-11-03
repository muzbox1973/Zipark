using System.Diagnostics;
using Zipark.Core;
using Zipark.VirtualDisk.Iso;

namespace Zipark.Tests;

public class IsoTests
{
    /// <summary>Builds a small ISO with the Windows IMAPI2 file-system image COM API (no third-party tools).</summary>
    internal static string CreateIso(string dir, string sourceDir, string label = "FREEARCX_TEST")
    {
        var iso = Path.Combine(dir, "test.iso");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
            $fsi.FileSystemsToCreate = 3
            $fsi.VolumeName = '{{label}}'
            $fsi.Root.AddTree('{{sourceDir}}', $false)
            $img = $fsi.CreateResultImage()
            $stream = $img.ImageStream
            if (-not ('IsoWriter' -as [type])) {
            Add-Type -TypeDefinition @'
            using System; using System.IO; using System.Runtime.InteropServices.ComTypes;
            public static class IsoWriter {
              public static void Save(object s, string path) {
                var st = (IStream)s; var buf = new byte[65536]; var read = Marshal.AllocHGlobal(4);
                try { using (var f = File.Create(path)) { while (true) { st.Read(buf, buf.Length, read);
                  int n = System.Runtime.InteropServices.Marshal.ReadInt32(read); if (n <= 0) break; f.Write(buf, 0, n); } } }
                finally { Marshal.FreeHGlobal(read); } } }
              static class Marshal { public static IntPtr AllocHGlobal(int n) { return System.Runtime.InteropServices.Marshal.AllocHGlobal(n); }
                public static void FreeHGlobal(IntPtr p) { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); } }
            '@
            }
            [IsoWriter]::Save($stream, '{{iso}}')
            """;
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(File.Exists(iso), "ISO creation failed: " + err);
        return iso;
    }

    [Fact]
    public async Task Iso_BrowseAndExtractWithEngine()
    {
        using var t = new TempDir("isob");
        var src = TestUtil.CreateSampleTree(t.Path, 100_000);
        var iso = CreateIso(t.Path, src);
        var engine = TestUtil.Engine();
        var info = await engine.ListAsync(iso, null);
        Assert.Equal(ArchiveFormat.Iso, info.Format);
        Assert.Contains(info.Entries, e => e.Path.EndsWith("random.bin"));
        var dest = t.Sub("x");
        await engine.ExtractAsync(new Compression.ExtractOptions { ArchivePath = iso, Destination = dest }, null);
        Assert.Equal(Checksums.Sha256OfFile(Path.Combine(src, "sub", "random.bin")), Checksums.Sha256OfFile(Path.Combine(dest, "sub", "random.bin")));
    }

    [Fact]
    public void Iso_MountReadUnmount_WithWindowsVirtualDiskApi()
    {
        using var t = new TempDir("isom");
        var src = TestUtil.CreateSampleTree(t.Path, 100_000);
        var iso = CreateIso(t.Path, src);
        var before = Checksums.Sha256OfFile(iso);
        IsoMount mount;
        try
        {
            mount = IsoMounter.Mount(iso);
        }
        catch (ZiparkException ex) when (ex.Code == ErrorCode.AccessDenied)
        {
            Assert.Fail("Mount denied for this account: " + ex.Message);
            return;
        }
        try
        {
            Assert.NotNull(mount.DriveLetter);
            var mounted = Path.Combine(mount.DriveLetter! + "\\", "sub", "random.bin");
            Assert.Equal(Checksums.Sha256OfFile(Path.Combine(src, "sub", "random.bin")), Checksums.Sha256OfFile(mounted));
            Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(mount.DriveLetter! + "\\", "new.txt"), "x"));
            Assert.NotNull(IsoMounter.Query(iso));
        }
        finally
        {
            IsoMounter.Unmount(iso);
        }
        Thread.Sleep(500);
        Assert.Null(IsoMounter.Query(iso)?.DriveLetter);
        Assert.Equal(before, Checksums.Sha256OfFile(iso));
    }
}
