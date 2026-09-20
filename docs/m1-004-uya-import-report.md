# M1-004 local UYA import report

Date: 2026-09-20  
Platform: Linux x64, .NET 10  
SDK revision: `0deb8869f7f080a7afd4543a663e20c02c344da8`  
Source: clean NTSC-U UYA ISO, 4,379,377,664 bytes, MD5
`ba9f2b38c7346e7b6e5b8e87717d5893`

The source ISO was opened read-only. The catalog was written to `/tmp` rather
than the user's Forge application-data directory.

## Result

| Metric | Result |
| --- | ---: |
| Populated levels scanned | 51 / 51 |
| Asset appearances | 10,665 |
| Unique assets | 7,224 |
| Assets with placeholder textures | 14 appearances / 2 unique |
| Assets skipped | 0 |
| Catalog footprint | 619 MiB |
| Clean import wall time | 16.12 seconds |
| Maximum resident memory | 523,764 KiB |
| Completed-checkpoint reopen | 1.65 seconds |
| Forced re-import | 13.31 seconds |

| Kind | Unique | Appearances |
| --- | ---: | ---: |
| Moby | 3,903 | 7,234 |
| Shrub | 652 | 688 |
| Tie | 2,669 | 2,743 |

Two mobys (`0x112F` and `0x1238`) contain texture references the pinned SDK cannot
decode. Importer v1 preserves all 14 appearances and substitutes a tagged
magenta-and-black checkerboard for each unresolved texture. A separate run was
interrupted after one level and resumed successfully from its checkpoint.

A forced re-import rescanned all 51 levels, reused the existing identity blobs,
and reproduced the same 10,665 appearances, 7,224 unique assets, and zero failures.
