# Zipark — Performance Report

Measured 2026-11-02 21:20 with `tools/Zipark.Benchmark` (median of 3 runs).

## Environment

- OS: Microsoft Windows NT 10.0.26200.0 (x64), 22 logical CPUs
- Engine: 7-Zip 26.05 (`C:\Program Files\7-Zip\7z.exe`)
- .NET 10.0.14
- Dataset: 169 files, 129 MB (≈40% text-like, ≈35% random, ≈25% repetitive binary)

## Compression / extraction

| Format / level | Tool | Compress (s) | Extract (s) | Archive size | Ratio | 7z peak RAM |
|---|---|---:|---:|---:|---:|---:|
| SevenZip / Normal | 7-Zip console | 70.13 | 0.92 | 91.1 MB | 70.8% | 567 MB |
| SevenZip / Normal | Zipark | 66.74 | 1.00 | 91.1 MB | 70.8% | (same engine) |
| Zip / Normal | 7-Zip console | 1.22 | 0.50 | 92.4 MB | 71.9% | 195 MB |
| Zip / Normal | Zipark | 1.22 | 0.59 | 92.4 MB | 71.9% | (same engine) |
| SevenZip / Fastest | 7-Zip console | 1.42 | 0.30 | 93.1 MB | 72.4% | 112 MB |
| SevenZip / Fastest | Zipark | 1.41 | 0.43 | 93.1 MB | 72.4% | (same engine) |

Zipark drives the same 7-Zip engine, so compression ratio is identical by design. Differences in time are the
wrapper's own work: listing + safety checks before extraction, extraction into a staging folder and moving files into
place, temp-file output, and progress parsing. **Zipark is not claimed to be faster than 7-Zip.**

## Virtual drive read path (archive file-system core, without WinFsp)

| Scenario | Result |
|---|---:|
| Open small file inside solid 7z (cold) | 763 ms |
| Same file again (cache hit) | 0.08 ms |
| Read largest file (2.81 MB), already decoded with its solid block | 0.00 s (5.32 GB/s) |
| Engine decode runs / bytes decoded | 1 / 103 MB |

The WinFsp kernel round-trip is not included (WinFsp was not installed on the measuring machine); see the release checklist.

## Notes on interpretation

- 7Z/Normal measured 66.7 s for Zipark vs 70.1 s for the plain console. Both run the identical `7z.exe` with the same
  `-mx`/`-mmt` settings and produce byte-identical sizes, so this ~5% gap is treated as run-to-run variance
  (turbo/thermal state, I/O cache), not as a speed advantage. Run order alternated between tools to limit bias.
- Extraction is 0.08–0.13 s slower with Zipark: the cost of listing + safety checks before extraction and of moving
  files out of the staging folder. This is the deliberate price of Zip Slip / archive-bomb protection.
- Cold open of a small file inside a 103 MB solid block took ~0.76 s because the block has to be decoded from its start;
  afterwards every file of that block is served from cache (0.08 ms).
- Measured on a single machine; numbers on other hardware will differ. Re-run with
  `dotnet run -c Release --project tools/Zipark.Benchmark -- --out docs/PERFORMANCE_REPORT.md`.
