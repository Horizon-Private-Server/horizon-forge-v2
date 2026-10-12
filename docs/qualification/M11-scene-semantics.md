# M11 scene semantics and level settings qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)

Date: 2026-10-11

Status: automated gate passed; packaged Linux/Windows and PCSX2 evidence pending

## Automated gate

Run `npm test`. The SDK, host, and renderer fixtures verify:

- death height, ship position/rotation, spherical-world center/state, ship path, and
  camera cuboids import and bake through their verified native byte ranges while
  unknown settings bytes remain unchanged;
- typed spline/cuboid references survive deterministic reorder, deletion, undo/redo,
  save, recovery, and native-index resolution;
- invalid numeric drafts and missing or wrong-kind references do not partially mutate
  authoritative state;
- ship and spherical-world billboards track authoritative coordinates, use distinct
  accessible glyphs, and edit through the ordinary transform command path;
- the death-height plane follows scene bounds, toggles without project mutation, stays
  outside picking/snapping/bake geometry, and disposes its shared resources; and
- areas appear only as map-owned group containers, transform resolved children
  atomically, follow the shared hide-empty setting, and allocate no scene proxy.

## Manual packaged-app matrix

Do not commit projects, extracted game data, patched ISOs, or local filesystem paths.
Retain the packaged Forge revision, platform, PCSX2 version, level, and local
`build-patch.log` reference for each completed row.

| Environment | Scenario | Expected result | Status |
| --- | --- | --- | --- |
| Linux packaged | Edit every promoted setting, exercise nullable references, save/reopen, undo/redo, and recovery | Values and reference labels remain authoritative; invalid drafts do not commit. | Development walkthrough reported working; packaged evidence pending |
| Linux viewport | Move ship and spherical-world gizmos, toggle death height, navigate non-empty areas, and transform an area | Gizmos remain responsive and distinct; the death overlay does not affect selection; area children move atomically with no area scene proxy. | Development walkthrough reported working; packaged evidence pending |
| Linux build/patch | Clean-build after representative settings edits | Only required layers rebuild; semantic re-read succeeds and the development ISO verifies. | Pending retained evidence |
| Linux + PCSX2 | Exercise death height, ship spawn/rotation/path/camera, and spherical-world settings | Runtime behavior agrees with Forge values without unrelated scene changes. | Pending |
| Windows packaged nightly | Transfer the project ZIP, repeat the settings and viewport walkthrough, and clean-build | Assets/references resolve and diagnostics/output agree with Linux. | Pending |

M11-005 remains open until the packaged Linux/Windows and PCSX2 rows record retained
evidence.
