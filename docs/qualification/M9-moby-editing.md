# M9 MobyDex and moby editing qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Date: 2026-10-10  
Status: automated synthetic gate passed; real-level pack/patch and PCSX2 checks pending

## Automated gate

Run `npm test`. The proprietary-data-free protocol fixtures verify:

- UYA instance edits patch only owned native record ranges and preserve all opaque bytes;
- malformed, incompatible, and stale MobyDex definitions reject before project mutation;
- PVar primitives, nested fields, fixed arrays, RGB color, typed moby references,
  relocations, and unknown gaps survive save and semantic native-table re-read;
- stable Entity-ID references survive project entity-list reordering and resolve to the
  final deterministic native moby index during staging;
- nullable deletion clears the reference and writes its declared native null sentinel,
  while unresolved non-null source indexes remain visible before bake normalization;
- structured edits participate in undo/redo, and failed edits leave bytes and reference
  state unchanged; and
- the shared References view receives the owning field path, signed encoded index,
  resolved endpoint, missing/null state, and active MobyDex provenance.

Schema tests separately cover declarative bounds, overlap rules, arrays, reference
targets, defaults, relocations, canonical formatting, malformed input, and bounded fuzz
data. Renderer tests cover indexed graph pagination and typed endpoint navigation.

## Manual qualification matrix

No proprietary level or patched ISO is committed. Retain local logs by path or CI run
identifier without adding extracted game data to the repository.

| Environment | Scenario | Expected result | Status |
| --- | --- | --- | --- |
| Packaged Linux | Edit representative retail moby instance fields and PVar primitive/color/array/reference fields, save, close, and reopen | Values and opaque bytes agree; References and raw-byte views show the same targets and encoded indexes. | Pending |
| Linux build/patch | Build and patch a development ISO after the edits, then semantically inspect the packed gameplay tables | Instance fields, PVar bytes, relocations, and final native references agree with the project; unrelated bytes and layers remain unchanged. | Pending |
| PCSX2 | Load that development ISO and exercise each edited moby | Edited behavior is visible without crashes or unrelated moby regressions. Record tester, date, level, Forge revision, and local build log. | Pending maintainer result |
| Windows packaged nightly | Repeat project reopen and build on the `windows-2022` artifact | Results and diagnostics agree with Linux. | Pending |

M9-009 and the M9 exit gate remain open until the real-level pack/patch and PCSX2 rows
have maintainer evidence.
