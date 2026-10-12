# M12 editable tfrag qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Date: 2026-10-11  
Status: automated Linux gate passed; packaged PCSX2 and Windows evidence pending

## Automated gate

Run `npm test` in Forge and `dotnet run --project
tests/RatchetPs2.TfragTests/RatchetPs2.TfragTests.csproj --configuration Release` in the
Ratchet PS2 SDK. The fixtures verify:

- byte-identical no-edit output for primary and streamed terrain owners;
- translation, arbitrary rotation, and positive uniform enlargement/reduction with
  semantic native re-read;
- one-owner edits preserve every unaffected owner and unrelated native group;
- repeated and clean staging produce the same output fingerprint;
- save/reopen, undo/redo, recovery restore, directory moves, and ZIP transfer preserve
  Entity IDs, transforms, and attached canonical assets;
- invalid transforms reject before mutation, while cancellation and invalid candidate
  output preserve the last successful stage;
- entity-aware rendering keeps exact occurrence picking while batching shared geometry;
  and
- generated individual/shared tfrag collision uses the canonical local mesh, follows
  piece transforms, survives save/reopen, and retains native face types, while coarse
  retail collision remains independent under the documented M12-006 deferral.

The local retail corpus covers 3,354 exact-boundary groups. The current largest-level
extraction fixture contains 1,006 occurrences across primary plus two streamed owners.
The level09 chunk 2 regression composes two simultaneous edited groups in a 1,271,408-
byte/445-group payload in about 2 ms with about 6.9 MiB cumulative managed allocation
on the reference development machine. These values are diagnostic baselines, not
portable release limits.

## Preview/native rounding limits

- Canonical preview values are rounded to six decimal places.
- Native position rows and VIF bases use integer units at `1 / 1024` world scale, so
  nearest-integer composition differs by at most `1 / 2048` world units per axis.
- Bounding-cube coordinates use 64 native-unit steps, so their nearest representable
  coordinate can differ by at most `0.03125` world units per axis.
- Mesh-sphere radii use integer native units, while centers remain native `f32` values.
- Stored light-normal angles use 256 steps per turn, giving at most half a step
  (`pi / 256` radians, about `0.703` degrees) per encoded angle.
- Render surface normals are regenerated from quantized positions. Small scaled pieces
  can therefore show a larger derived-normal delta without changing stored topology or
  lighting data.

## Manual packaged-app matrix

Do not commit projects, extracted game data, patched ISOs, or local filesystem paths.
Retain the packaged Forge revision, platform, PCSX2 version, level/piece IDs, and
`build-patch.log` reference for each completed row.

| Environment | Scenario | Expected result | Status |
| --- | --- | --- | --- |
| Linux packaged | Transform representative primary and streamed pieces using translation, arbitrary rotation, uniform reduction, and uniform enlargement; save, close, reopen, and clean-build | Preview transforms persist; only tfrags rebuild; repeated clean output is deterministic. | Development walkthrough reported working; packaged evidence pending |
| Linux build/patch | Build level09 with simultaneous chunk 2 groups 2 and 53 edits | Preflight passes, the development ISO verifies, and unrelated terrain/collision remain unchanged. | Development build reported working; retained log reference pending |
| Linux generated collision | Generate individual collision for primary and streamed pieces, transform each piece, save/reopen, build, and inspect in PCSX2 | Generated collision follows the terrain transform, painted IDs persist, and combined native octant validation passes. | Automated composition gate passed; packaged PCSX2 evidence pending |
| Linux + PCSX2 | Inspect transformed primary and streamed pieces from several angles and distances | Placement, orientation, scale, texturing, and culling agree with preview within the rounding limits above; level remains stable. | Primary visual walkthrough passed; streamed-piece sign-off pending |
| Linux recovery | Save an edit, create a recovery state, cancel/fail a later build, restore, and rebuild | The last good stage/ISO remains usable and recovery restores the saved transform. | Automated gate passed; packaged walkthrough pending |
| Windows packaged nightly | Transfer the ZIP, reopen, clean-build, patch, and inspect the same pieces | Assets resolve without external paths and output/diagnostics agree with Linux. | Pending |
| Real PS2 hardware | Load the patched development image and inspect the same primary/streamed cases | No geometry, texture, culling, or stability regression appears on hardware. | Optional pre-release evidence pending |

M12-008 remains open until the packaged Linux/Windows and streamed PCSX2 rows record
retained evidence. Real-hardware evidence is desirable but is not required by the
current M12 exit gate.
