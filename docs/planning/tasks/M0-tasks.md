# M0 task register — Foundations and walking skeleton

Milestone: [M0 — Foundations](../milestones/M0-foundations.md)

## M0-001 — Scaffold the application boundaries

Status: ✅ Complete

Requirements: FR-APP-001, FR-APP-002, NFR-MAINT-001, NFR-MAINT-002, NFR-MAINT-003  
Depends on: none

Deliver the smallest runnable Electron/Vite/React/Mantine/Three.js shell plus a
.NET 10 host solution. Keep Electron main, preload, renderer, and .NET projects
separate; pin dependencies and commit lockfiles.

Acceptance:

- Development mode opens one window and renders a Three.js canvas.
- The renderer has Node integration disabled, context isolation and sandboxing on.
- The .NET host builds independently on Linux and Windows.
- Project boundaries match the specification and no game logic is duplicated.

Verification: typecheck/build both stacks and launch the development shell.

## M0-002 — Bootstrap the pinned Ratchet SDK

Status: ✅ Complete

Requirements: FR-SDK-001, DR-015, NFR-REL-004  
Depends on: M0-001

Add the SDK revision file and one bootstrap script that accepts an optional local
source path or fetches the configured ratchet-ps2-cli revision into an ignored
dependency directory, verifies it, and builds the required .NET projects.

Acceptance:

- No NuGet Ratchet package or Git submodule is introduced.
- Fresh, cached, mismatched, missing, and local-source cases have clear behavior.
- Forge builds against the installed revision and reports it at runtime.
- The script is non-interactive and usable unchanged in GitHub Actions.

Verification: automated bootstrap smoke using a local source override plus a
negative revision-mismatch check.

## M0-003 — Specify and implement binary bridge framing

Status: ✅ Complete

Requirements: FR-BRIDGE-001, FR-BRIDGE-002, FR-BRIDGE-003, FR-BRIDGE-004, NFR-SEC-003  
Depends on: M0-001

Write the byte-level protocol document and matching TypeScript/C# frame codecs for
handshake, request, result, progress, cancellation, and error frames.

Acceptance:

- Header sizes, byte order, limits, opcodes, versions, and error codes are explicit.
- Pipe fragmentation/coalescing cannot corrupt message boundaries.
- Invalid magic/version/length fails without unbounded allocation.
- stdout is protocol-only and stderr remains diagnostic text.

Verification: shared golden vectors, fragmented-read tests, oversized/malformed
frames, and cross-language round trips.

## M0-004 — Manage the host and secure renderer bridge

Status: ✅ Complete

Requirements: FR-BRIDGE-003, NFR-SEC-001, NFR-SEC-002, NFR-PERF-004, NFR-MAINT-004  
Depends on: M0-002, M0-003

Spawn and supervise one host process from Electron main and expose only named,
validated operations through preload.

Acceptance:

- Handshake gates all requests and reports host/SDK capabilities.
- Pending calls fail clearly on crash; restart restores service without app restart.
- Progress/cancellation correlate by request ID and streams are continuously drained.
- Renderer cannot access raw IPC, filesystem, shell, or child-process APIs.

Verification: forced crash/restart, concurrent echo/progress requests, cancellation,
backpressure, sender validation, and CSP/navigation checks.

## M0-005 — Freeze version-zero identity and schema fixtures

Status: ✅ Complete

Requirements: FR-ASSET-001, FR-ASSET-002, NFR-REL-004, NFR-PORT-001  
Depends on: M0-001

Define version-zero domain separation/canonical bytes for initial Asset IDs,
Entity-ID generation, project schema version fields, and migration rejection rules.

Acceptance:

- Equivalent canonical inputs hash identically across Linux/Windows.
- Asset kind/version prevent cross-domain collisions.
- Entity IDs survive serialization while duplicates receive new IDs.
- Unsupported future schema data is never overwritten.

Verification: fixed SHA-256 vectors and project/entity serialization fixtures.

## M0-006 — Audit map-o-matic reuse

Status: ✅ Complete

Requirements: FR-SCENE-001, FR-SCENE-002, NFR-MAINT-001, NFR-PERF-003, NFR-PERF-005  
Depends on: M0-001

Trace map-o-matic's map loading, package formats, renderers, workers, resource
ownership, and UI coupling. Produce an ADR listing code to port, adapt, or leave.

Acceptance:

- Every proposed reused module has dependencies and license/provenance recorded.
- Known allocation, disposal, draw-call, and backend-specific problems are listed.
- The migration order preserves a runnable viewport after each step.
- No code is copied merely because it exists.

Verification: reviewed ADR linked from the M0 milestone.

## M0-007 — Benchmark WebGL versus WebGPU

Status: ✅ Complete

Requirements: FR-SCENE-001, NFR-PERF-001, NFR-PERF-003, OD-013  
Depends on: M0-006

Build equivalent WebGLRenderer and WebGPURenderer benchmark paths for one light and
one heavy representative UYA scene.

Acceptance:

- Both paths use equivalent assets, camera, resolution, effects, and warm-up.
- Report startup, median/1%-low frame time, CPU/GPU time where available, memory,
  compatibility, and visible correctness on declared hardware/drivers.
- An ADR selects one P0 backend and states why.
- The losing backend is not retained as speculative production code.

Verification: reproducible benchmark command, raw results, screenshots, and ADR.

Resolution: [ADR 0002](../../adr/0002-webgl-p0-renderer.md) selects classic
WebGL for P0. The project owner ended further A/B work after the experimental
paths proved non-equivalent; the original quantitative comparison is superseded.

## M0-008 — Package the viewport walking skeleton

Status: ✅ Complete

Requirements: FR-SCENE-001, FR-SCENE-002, FR-BRIDGE-004, NFR-PERF-005  
Depends on: M0-004, M0-005, M0-006, M0-007

Port the minimum audited viewer/package path into Forge and load a representative
non-proprietary or locally supplied UYA render package through the selected backend.

Acceptance:

- Packaged Linux app starts host and displays the scene without a dev server.
- Large render data avoids redundant renderer/main/host copies.
- Closing/reloading disposes scene and GPU resources.
- Missing local test data produces a guided empty state, not a crash.

Verification: packaged smoke, repeated-load memory check, and manual visual record.

Resolution: the Linux x64 package embeds the .NET host/runtime, streams a local
`terrain.gltf` directory through the constrained `forge-asset:` protocol, and
uses WebGL exclusively. On 2026-09-19 the packaged app completed the host
handshake, rendered UYA level 3, survived five page reloads at a reported 86.4 MB
JS heap, and displayed the guided missing-path state without a fixture. That
diagnostic terrain path was later removed when the editor scene was rebuilt around
authoritative project entities; its production replacement is M2-010.

## M0-009 — Establish continuous integration

Requirements: FR-RELENG-001, NFR-SEC-005, NFR-MAINT-003  
Depends on: M0-002, M0-003, M0-008

Create least-privilege GitHub Actions for locked frontend install, pinned SDK
bootstrap, .NET/frontend checks, and Linux/Windows build matrices.

Acceptance:

- Pull requests and pushes build/test without publishing releases.
- Third-party actions are pinned to reviewed immutable revisions.
- Cache keys include lockfiles and SDK revision; mismatches cannot reuse output.
- Failures upload useful logs and packaged smoke runs on Linux.

Verification: successful clean workflow plus an intentional failing-branch proof.

Implementation: `.github/workflows/ci.yml` runs locked installs, pinned SDK
bootstrap, and the full test suite on Ubuntu 24.04 and Windows 2022. Linux also
builds the self-contained package and runs it under Xvfb. The package smoke
passes locally; the successful hosted run and intentional failing-branch proof
remain pending until this work is committed and pushed.
