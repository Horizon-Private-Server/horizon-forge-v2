# M4 task register — Build, patch, and test

Milestone: [M4 — Build, patch, and test](../milestones/M4-build-patch.md)

## M4-001 — Pack staged output into a UYA level WAD

Requirements: FR-BUILD-001, FR-BAKE-004, NFR-REL-002
Depends on: M3-008

Integrate the pinned SDK packer so a validated staging snapshot becomes a valid
NTSC-U UYA level WAD without routing bulk data through Electron IPC.

Acceptance:

- Packing consumes only a complete, validated staging manifest.
- The host streams progress and structured diagnostics while the SDK owns file I/O.
- Resulting archives reopen successfully and match staged content semantically.
- Cancellation or failure leaves no output that can be mistaken for complete.

Verification: archive re-read tests, cancellation injection, and golden fixture comparison.

## M4-002 — Validate the development ISO and create a patch plan

Requirements: FR-PATCH-001, FR-PATCH-002, DR-004, DR-008
Depends on: M4-001, M1-002

Identify the selected ISO, prove it is the supported NTSC-U UYA development copy,
protect configured clean sources, and calculate all writes before mutation.

Acceptance:

- Clean-source paths and matching file identity are always rejected as patch targets.
- Wrong game, region, revision, layout, permissions, or capacity fails before writes.
- The patch plan records expected source bytes/checksums, offsets, lengths, and result.
- A valid development ISO can be checked while PCSX2 is not managed by Forge.

Verification: fixture matrix for clean, copied, unsupported, corrupt, and unwritable ISOs.

## M4-003 — Implement journaled in-place ISO patching

Requirements: FR-PATCH-003, NFR-REL-001, NFR-SEC-001
Depends on: M4-002

Apply a validated patch plan directly to the development ISO with a recovery journal,
bounded memory, flush verification, and no clean-ISO mutation path.

Acceptance:

- The journal is durable before the first ISO write and records restoration data.
- Every write verifies expected preimage and written result before commit advances.
- Startup detects incomplete work and offers deterministic recovery or revalidation.
- File-lock and permission errors are surfaced without corrupting the prior image.

Verification: fault injection at every journal phase and clean-source immutability test.

## M4-004 — Add full-image fallback and final ISO verification

Requirements: FR-PATCH-003, NFR-REL-001
Depends on: M4-002, M4-003

When in-place patching is unsafe or the new payload does not fit, build a verified
replacement beside the development ISO and atomically swap it where supported.

Acceptance:

- Fallback selection is explained before the longer copy begins.
- Free-space checks include temporary and final image requirements.
- The original development ISO remains recoverable until replacement verifies.
- Final verification checks image structure and the installed level payload.

Verification: insufficient-space, forced-fallback, interrupted-copy, and verify-failure tests.

## M4-005 — Build the one-click bake, pack, and patch orchestrator

Requirements: FR-PATCH-004, FR-PATCH-005, FR-UI-005
Depends on: M3-008, M4-001, M4-003, M4-004

Expose one editor action that plans required bakes, packs the WAD, patches the selected
development ISO, and presents phase-aware progress, cancellation, and recovery guidance.

Acceptance:

- Up-to-date layers are skipped and each phase reports determinate progress when known.
- Cancellation is honored only at safe boundaries and reports the resulting state.
- Errors retain logs and offer the next valid retry/recovery action.
- Success identifies the patched development ISO; no PCSX2 launch/reload is attempted.

Verification: orchestration state-machine tests and successful UI-driven workflow.

## M4-006 — Qualify the external PCSX2 test loop

Requirements: section 8.7, FR-PATCH-001 through FR-PATCH-005, DR-007
Depends on: M4-005

Document and execute the supported workflow in which users configure PCSX2 themselves,
patch the development ISO in Forge, and reload it externally on Linux and Windows.

Acceptance:

- A patched ISO boots and loads the edited NTSC-U UYA level in PCSX2.
- Linux patching works when the image is selected by PCSX2 and not OS-locked.
- Windows sharing violations produce a clear close/eject-and-retry instruction.
- The qualification includes no-op, normal, fallback, cancel, and recovery paths.

Verification: signed-off Linux and Windows milestone checklist with retained logs.
