# M2 skybox editor qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Status: ✅ Passed

## Automated gate

Run `npm test`. The synthetic end-to-end fixture verifies that:

- adding, editing, reordering, and removing shells invalidates only the Sky layer;
- initial rotation and rotational velocity survive save/reopen and native tick conversion;
- a corrupted composed output cannot replace the last known-good staged Sky;
- the edited sky packs and semantically re-reads with the expected order and rotations; and
- restoring the imported composition restores the byte-identical level WAD.

The shared build/patch suite separately verifies cancellation, failed staging, and
development-ISO preservation and recovery.

## Manual PCSX2 gate

Recorded run: 2026-09-29, project owner, Bakisi4, Forge 2.0.0, PCSX2 2.5.122
AppImage. The tester confirmed all results live in-game. The local diagnostic log is
`~/.config/horizon-forge-v2/logs/build-patch.log`; successful builds do not append a
log entry. No screenshot was retained for this run.

Use the packaged-app procedure in the
[M4 external PCSX2 loop qualification](M4-pcsx2-test-loop.md). Perform the following
edits in one visible level, building and externally reloading the development ISO
after each row. Do not use a savestate after a full-image replacement.

| Scenario | Edit | Expected result | Status |
| --- | --- | --- | --- |
| Cross-level add | Add one visually distinct shell from another compatible level. | The level boots and the new shell renders with correct geometry and textures. | Passed. |
| Stationary rotation | Set a non-zero initial rotation and zero rotation speed. | The shell renders at the edited fixed orientation. | Passed. |
| Animated rotation | Set an obvious non-zero rotation speed on one axis. | The shell rotates smoothly in the requested direction while gameplay remains stable. | Passed. |
| Reorder | Move two visibly overlapping shells into the opposite order. | The rendered blend/draw order changes to match the editor list. | Passed. |
| Remove | Remove one shell and rebuild. | The removed shell is absent and the remaining sky renders normally. | Passed. |

M2-030 is complete: all five manual rows and the automated gate passed.
