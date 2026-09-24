# M4 external PCSX2 loop qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Status: in progress — Windows and full-image manual checks remain

Forge owns the development ISO through final verification. PCSX2 remains externally
configured and is never launched, paused, or reloaded by Forge.

## Automated gate

Run `npm test` on Linux and Windows. The synthetic contract suite verifies:

- normal and repeated no-op one-click builds;
- journaled in-place patching and final installed-level verification;
- automatic and forced full-image replacement;
- cancellation before mutation and during replacement copying;
- recovery after every durable in-place patch phase, including automatic recovery
  when the next one-click build finds an unfinished journal;
- preservation of the clean ISO and last-known-good development ISO; and
- actionable `Close/eject it from PCSX2 and retry.` guidance when the development
  ISO cannot be opened for writing.

## Manual packaged-app matrix

Use a clean UYA ISO only as the configured source and point PCSX2 at Forge's separate
development ISO. Retain `build-patch.log` for any failure. Do not attach either ISO.

| Platform | Scenario | Procedure | Expected result | Status |
| --- | --- | --- | --- | --- |
| Linux | Normal in-place edit | Move an obvious tie and moby, build, then reload the development ISO in PCSX2. | The level boots and both edits are visible. | Passed: level 41 edit confirmed 2026-09-23. |
| Linux | ISO already open | Keep the development ISO selected in PCSX2 and build again. | Forge patches and verifies without managing PCSX2; an external reload shows the edit. | Pending repeatable sign-off. |
| Linux | No-op | Build twice without another edit. | The second build reports zero baked layers and the level still loads. | Pending manual sign-off; automated gate passed. |
| Linux | Full-image fallback | Use a map change whose packed WAD exceeds its current allocation, then build. | Forge explains replacement, verifies it, and normal boot loads the map; old savestates are not used. | Pending. |
| Linux | Cancel/recover | Cancel during a long build, then build again. | The prior ISO remains usable or is restored before the retry completes. | Pending manual sign-off; automated gate passed. |
| Windows | Normal/no-op | Repeat the normal and no-op cases from the packaged nightly. | Both builds verify and PCSX2 loads the edited level. | Pending. |
| Windows | Sharing violation | Keep the ISO open in a mode that denies writes, then build. | Forge leaves both ISOs intact and says to close/eject the ISO from PCSX2 and retry. | Pending manual sign-off; automated message check passed. |
| Windows | Full-image/cancel/recover | Repeat the fallback and interrupted-build cases. | Replacement is atomic, cancellation retains the prior image, and retry recovers safely. | Pending. |

M4-006 is complete only when every pending manual row has a tester, date, packaged
Forge version, PCSX2 version, result, and retained local log reference.
