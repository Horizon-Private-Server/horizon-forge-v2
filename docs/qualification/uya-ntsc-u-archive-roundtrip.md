# UYA NTSC-U archive round-trip qualification

Generated: 2026-09-23 UTC  
Runtime: .NET 10.0.11, Bazzite x64, 32 logical processors  
Machine-readable report: [uya-ntsc-u-archive-roundtrip.json](uya-ntsc-u-archive-roundtrip.json)

The clean 4,379,377,664-byte NTSC-U ISO contained 51 populated level entries.
All 51 completed the in-memory workflow with 3,162 matching pre-compression and
decoded-result SHA-256 checks, zero mismatches, and zero diagnostics. No game
bytes or filesystem paths are retained in either report.

| Metric | Result |
| --- | ---: |
| Source level data | 823.11 MiB |
| Repacked level data | 797.29 MiB |
| Total measured build time | 29.55 s |
| Minimum throughput | 24.03 MiB/s |
| Median throughput | 27.15 MiB/s |
| Median managed allocation | 463.16 MiB |
| Maximum managed allocation | 539.31 MiB |
| Median sampled process working set | 469.77 MiB |
| Maximum sampled process working set | 765.68 MiB |
| Generated compression ratio | 45.46–56.98% |
| Assembly bulk-buffer copies per level | 5 |

Managed allocation is cumulative allocation during one build, not simultaneous
live memory. Working set is sampled during each build and includes the runtime and
retained GC heap, so performance regressions should be compared on the same machine.
The assembly copy count is one pre-compression root, three decoded child-container
destinations, and one final root; it is independent of payload count.

Run the same qualification with:

```bash
dotnet run --project tests/RatchetPs2.LevelTests/RatchetPs2.LevelTests.csproj \
  --configuration Release -- \
  --qualify-uya-iso <clean-ntsc-u-iso> <report.json>
```
