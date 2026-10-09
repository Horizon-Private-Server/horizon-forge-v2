# M7 authoring foundation qualification

Target: Forge 2.0.0 development, UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Date: 2026-10-07  
Status: automated and packaged-Linux smoke gates passed; Windows and remaining UI walkthroughs pending

## Automated portability gate

Run `npm test`. `AuthoringFoundationQualificationTests` verifies:

- every project schema from v0 through v13 opens as the current in-memory model
  without changing either stored document;
- the normal journaled save upgrades each version to compressed v13 content and
  removes an obsolete pre-v4 content path only after the save completes;
- a future manifest/content pair is rejected without either file being overwritten;
- historical document-type omissions, uncompressed content, TIE collision field
  names, collision attachment names, and area links migrate to current contracts;
- an overridden synthetic texture and typed spline/cuboid references survive
  undo/redo, recovery restore, explicit save, ZIP transfer, and deterministic
  asset/native-index resolution; and
- the transferred project resolves its replacement bytes from its project-relative
  attached-asset store.

The renderer suite separately verifies the ribbon/reference state helpers and shared
billboard projection, picking, bounded screen sizing, robot glyph selection, cache
reuse, synchronous icon changes, and complete GPU-resource disposal.

## Packaged application matrix

The Linux package was rebuilt from working-tree base commit
`3ed7d8b4cbd9033aa8b7726c6314a9c9b70849d1` and passed
`scripts/smoke-package-linux.sh`. The smoke log is generated locally at
`artifacts/ci-logs/m7-authoring-foundation-linux-smoke.log` and is intentionally not
versioned.

| Gate | Result |
| --- | --- |
| Linux x64 package/build and isolated-settings startup | Passed |
| Billboard/map-pin visual inspection | Passed before the robot-glyph refinement |
| Linux ribbon keyboard and 125–200% scaling walkthrough | Pending |
| Linux References keyboard and high-DPI walkthrough | Pending |
| Robot glyph packaged visual recheck | Pending |
| Windows x64 build/test/package CI | Pending |
| Windows ribbon, References, and billboard keyboard/high-DPI walkthrough | Pending |

M7-007 remains open until the pending rows pass. The Windows package must be produced
on the repository's `windows-2022` CI job; the Linux Electron distribution cannot be
used to manufacture a representative Windows package.
