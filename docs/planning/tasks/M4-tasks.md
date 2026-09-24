# M4 task register — Build, patch, and test

Milestone: [M4 — Build, patch, and test](../milestones/M4-build-patch.md)

## M4-001 — Pack staged output into a UYA level WAD

Requirements: FR-SDK-002, FR-BUILD-001 through FR-BUILD-004, FR-BAKE-004, NFR-REL-002
Depends on: M3-008, M3A-006, M5-003

Map a validated staging snapshot into the SDK archive image proven by M3A so it
becomes a valid NTSC-U UYA level WAD without routing bulk data through Electron
IPC or reimplementing container layout in Forge.

Acceptance:

- Packing consumes only a complete, validated staging manifest.
- The host streams progress and structured diagnostics while SDK/game assemblies
  own binary layout, compression, and verification.
- Generated and opaque regions completely populate the SDK byte-ownership map.
- Resulting archives reopen successfully, match staged content semantically, and
  preserve every untouched opaque payload hash.
- Cancellation or failure leaves no output that can be mistaken for complete.

Verification: archive re-read tests, cancellation injection, and golden fixture comparison.

Implementation: `UyaLevelPackService` accepts only a fully clean validated staging
manifest, maps native gameplay, instance, Pvar, settings, and lighting payloads to
SDK logical paths, and calls `UyaLevelArchiveBuilder` in-process. It reopens the
result, compares every replacement semantically, verifies the complete opaque
inventory by source hash, and routes resized sky, primary/chunk tfrag, and collision
payloads through the SDK-owned relocatable asset composer.
SDK progress and diagnostics are retained, cancellation publishes no output, and a
synthetic unchanged build is pinned by a golden WAD checksum. Cross-level model
injection remains deliberately blocked until M5-003 installs and remaps its model
definition, textures, palettes, and pixel indices as one validated operation; source
texture IDs are never reused speculatively.

## M4-002 — Validate the development ISO and create a patch plan

Requirements: FR-SDK-002, FR-PATCH-001, FR-PATCH-002, DR-004, DR-008
Depends on: M4-001, M1-002

Identify the selected ISO, prove it is the supported NTSC-U UYA development copy,
protect configured clean sources, and calculate all writes before mutation.

Acceptance:

- Clean-source paths and matching file identity are always rejected as patch targets.
- Wrong game, region, revision, layout, permissions, or capacity fails before writes.
- The SDK patch plan records expected source bytes/checksums, offsets, lengths,
  alignment, capacity, and expected result.
- A valid development ISO can be checked while PCSX2 is not managed by Forge.

Verification: fixture matrix for clean, copied, unsupported, corrupt, and unwritable ISOs.

Implementation: `UyaIsoPatchPlanner` in the SDK parses the target level table/header,
verifies the packed level identity and payload base, calculates sector capacity, and
emits immutable header/payload ranges with alignment, preimage SHA-256, output SHA-256,
and expected loose-WAD hashes. Oversized output is labeled for full-image fallback and
publishes no unsafe in-place ranges. `UyaIsoPatchService` resolves symlinks, holds the
clean source read-only, compares Linux/Windows filesystem object identities to reject
hard-link aliases, opens the development image read/write without managing PCSX2, and
requires the UYA NTSC-U 1.00 serial, region, revision, size, and valid level layout.
Fixtures cover clean copies, direct and hard-link source aliases, unsupported discs,
corrupt layouts, unwritable images, deterministic aligned writes, and capacity fallback.
A read-only clean-ISO audit resolved level03 to a 0x800-byte header plus 0x10A3800-byte
payload within its declared 8,520-sector capacity.

## M4-003 — Implement journaled in-place ISO patching

Requirements: FR-SDK-002, FR-PATCH-003, NFR-REL-001, NFR-SEC-001
Depends on: M4-002

Apply a validated patch plan directly to the development ISO with a recovery journal,
bounded memory, flush verification, and no clean-ISO mutation path.

Forge.Host owns path validation, file opening, and durable journal lifecycle. The
SDK owns range preconditions, binary mutation, and result verification over the
host-provided seekable stream.

Acceptance:

- The journal is durable before the first ISO write and records restoration data.
- Every write verifies expected preimage and written result before commit advances.
- Startup detects incomplete work and offers deterministic recovery or revalidation.
- File-lock and permission errors are surfaced without corrupting the prior image.

Verification: fault injection at every journal phase and clean-source immutability test.

Implementation: `UyaIsoPatchApplier` validates every planned preimage, performs one
bounded range write at a time, and verifies its SHA-256 result. `UyaIsoPatchService`
creates a durable sidecar journal containing verified preimage backups before mutation,
flushes each verified range before advancing the journal, and removes it only after full
output verification. An interrupted journal is classified as original, patched, partial,
or diverged and can restore the recorded preimage; a fully verified patched image can
instead be accepted. Tests inject failure at journal creation, range durability, commit
advance, and final verification, exercise cancellation at a safe boundary, and confirm
the protected clean ISO remains byte-identical.

## M4-004 — Add full-image fallback and final ISO verification

Requirements: FR-SDK-002, FR-PATCH-003, NFR-REL-001
Depends on: M4-002, M4-003

When in-place patching is unsafe or the new payload does not fit, build a verified
replacement beside the development ISO and atomically swap it where supported.

Acceptance:

- Fallback selection is explained before the longer copy begins.
- Free-space checks include temporary and final image requirements.
- The original development ISO remains recoverable until replacement verifies.
- Final verification checks image structure and the installed level payload.

Verification: insufficient-space, forced-fallback, interrupted-copy, and verify-failure tests.

Implementation: the SDK plan now normalizes the packed WAD to the development
image's physical sector layout and publishes an explained full-image replacement,
its final size, and required free space when the existing allocation cannot be used
or fallback is forced. `UyaIsoReplacementBuilder` copies with a bounded buffer,
relocates the WAD to appended sectors, updates the level table and ISO volume size,
then reopens the installed level to verify its SHA-256. Forge.Host writes and flushes
a sibling temporary image, validates its UYA identity and installed payload, confirms
the planned destination file is unchanged, and only then atomically replaces it.
Low-space, cancellation, corrupt-output, forced-fallback, repeated-plan, and
clean-source immutability fixtures retain the previous development image on failure.

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

Implementation: `UyaBuildPatchService` now owns the single in-process sequence from
incremental bake through SDK packing, patch planning, journaled application, and final
verification. A binary bridge operation exposes phase-aware progress and cancellation;
Electron saves the open project, resolves the configured clean/development ISO pair,
prompts for warning acknowledgement, retains failures in `build-patch.log`, and never
controls PCSX2. Both Forge menus and the editor status bar expose Build & Patch, live
determinate progress, cancellation, and the verified development-ISO result. Synthetic
state-machine coverage proves successful patching, clean-layer skipping, clean-source
immutability, safe cancellation state reporting, and bridge payload round trips.

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
