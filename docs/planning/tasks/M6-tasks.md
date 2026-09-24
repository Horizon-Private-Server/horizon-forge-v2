# M6 task register — Distribution and release qualification

Milestone: [M6 — Distribution](../milestones/M6-distribution.md)

## M6-001 — Package self-contained Linux and Windows applications

Requirements: FR-APP-001, FR-APP-002, FR-SDK-001, NFR-PORT-002
Depends on: M4-006, M5-005

Produce installable Linux and Windows artifacts containing the Electron/React app,
compatible .NET runtime/host, and pinned Ratchet SDK outputs without proprietary data.

Acceptance:

- Packages run on supported clean machines without a separately installed .NET or SDK.
- The packaged host and bridge protocol versions match the desktop application.
- Source/development ISOs and imported global assets are never bundled.
- Install, launch, upgrade, and uninstall preserve user projects and global asset stores.

Verification: clean Linux/Windows VM installation and smoke-test matrix.

## M6-002 — Publish uniquely versioned main-branch nightlies

Requirements: FR-RELENG-002, FR-RELENG-004, DR-010, NFR-SEC-005
Depends on: M0-009, M6-001

Extend GitHub Actions so every successful `main` commit publishes isolated prerelease
Linux and Windows packages and nightly update metadata.

Acceptance:

- Pull requests and non-main branches build/test but cannot publish.
- Each nightly has a unique SemVer prerelease version and full commit identity.
- Artifact metadata includes Forge, SDK, protocol, platform, architecture, and channel.
- Least-privilege jobs use pinned actions and a documented artifact retention policy.

Verification: dry-run branch/main workflows and metadata inspection of a published nightly.

Implementation: successful `main` pushes build self-contained Linux and Windows
archives, assign `2.0.0-nightly.<run>.<short-sha>`, embed channel/commit/SDK/protocol
metadata, publish checksums as a GitHub prerelease, and retain intermediate Actions
artifacts for 14 days. Pull requests and non-main branches package for validation
without receiving a release version or publication credentials. Each published
nightly also carries a machine-readable, channel-scoped release manifest containing
the full commit, SDK revision, bridge protocol, platform, architecture, size, and hash.

## M6-003 — Publish stable tagged releases

Requirements: FR-RELENG-003, FR-RELENG-004, NFR-SEC-004, NFR-SEC-005
Depends on: M6-001, M6-002

Create the manual `vMAJOR.MINOR.PATCH` path that validates version identity, runs the
full release gate, and publishes stable packages, checksums, provenance, and metadata.

Acceptance:

- A metadata/tag mismatch fails before publication.
- Re-running a tag cannot create a different release identity.
- Stable packages are non-prerelease and cannot consume nightly update metadata.
- Signing credentials, when configured, exist only in protected release environments.

Verification: non-publishing workflow tests plus one maintainer-approved release rehearsal.

Implementation: a pushed final SemVer tag must exactly match `package.json` before
either platform packages. The existing Linux/Windows matrix runs the full test gate,
embeds the stable channel and tag version, and uploads the same package contract used
by nightlies. A protected `stable-release` job publishes checksums and a deterministic
provenance/update manifest as a non-prerelease GitHub Release with generated notes.
Generated notes are anchored to the previous published stable release and grouped by
the repository's release-note label categories, so intervening nightlies do not become
the stable changelog baseline.
An existing published stable tag is validated and left untouched on rerun; a draft or
prerelease collision fails instead of mutating release identity. Signing credentials
are intentionally absent until platform signing is configured, and the stable job's
environment is the only future credential boundary.

## M6-004 — Implement consent-based update checks

Requirements: FR-UPD-001, FR-UPD-002, FR-UPD-003, NFR-SEC-004
Depends on: M6-002, M6-003

Add manual and periodic channel-aware update discovery, explicit user consent, and the
appropriate installation handoff for each supported platform.

Acceptance:

- Prompt shows trusted source, version, channel, notes, and download size.
- Forge never installs, restarts, changes channel, or downgrades without explicit consent.
- Unsaved work blocks restart and deferral remains available.
- Windows uses verified package updates; Linux clearly hands off where packaging requires it.

Verification: signed/invalid metadata, channel isolation, defer, unsaved-work, and install tests.

Implementation: packaged builds default to their embedded release channel, while an
explicit Settings choice allows users to switch between stable and nightly. Checks
remain isolated to the selected channel against the fixed Horizon Forge GitHub
release source. Manual checks are available from the Forge menu and periodic checks
are enabled by default in Settings. Automatic checks run passively after the renderer
loads and are never awaited by editor startup. Manual checks open a non-blocking
Mantine status modal while discovery runs. Validated updates enter the shared header
notification center with their source, version, channel, notes, size, and an explicit
handoff action; dismissing the notification defers the update. Linux opens the trusted
release page. Windows downloads the selected archive without restarting Forge and
accepts it only when its byte length and SHA-256 match the release manifest. Dirty
projects are called out explicitly and Forge never closes or replaces itself. Native
package signing remains the stable-release environment's future credential gate.

## M6-005 — Establish performance and memory release baselines

Requirements: NFR-PERF-001 through NFR-PERF-005
Depends on: M2-009, M4-005, M5-005

Define representative largest-supported fixtures and record reproducible viewport,
interaction, background-work, and repeated-open memory baselines on target hardware tiers.

Acceptance:

- The matrix records hardware, project fixture, settings, median, and 1% low frame times.
- Recommended hardware meets the provisional 60 FPS goal and low-end remains usable near 30 FPS.
- Background bake/pack/patch work does not stall Electron loops beyond declared limits.
- Repeated project opens and closes show no unbounded host or GPU resource growth.

Verification: versioned benchmark reports generated by documented scripts and soak procedure.

Implementation: `npm run performance:report` validates raw captures and produces
deterministic JSON and Markdown release reports with median and 1% low frame timing,
interaction p95, separate Electron main/renderer loop-delay p99 values, and ten-cycle
Forge.Host/renderer/GPU memory growth and trends. The documented qualification procedure
defines the largest-fixture selection, sample minimums, provisional release limits, and
redaction rules. M6-005 remains open until matching packaged-app captures pass on both
recommended and low-end hardware.

## M6-006 — Complete accessibility, diagnostics, and support documentation

Requirements: NFR-UX-001 through NFR-UX-003, NFR-MAINT-004, NFR-REL-003
Depends on: M2-009, M4-005, M6-001

Finish keyboard/high-DPI usability, safe destructive-action language, cancellable long work,
structured logs, redacted support bundles, and concise install/recovery documentation.

Acceptance:

- A keyboard-only pass covers setup, open, select/edit, bake, pack, and patch.
- Supported scaling and text-size settings keep critical controls reachable.
- Support bundles preview and redact paths/source metadata before export.
- Documentation covers setup, development ISO recovery, updates, and known limitations.

Verification: accessibility checklist, support-bundle redaction tests, and doc walkthrough.

## M6-007 — Run the P0 release qualification gate

Requirements: section 12 P0/MVP acceptance, all P0 functional and non-functional requirements, DR-016
Depends on: M6-003, M6-004, M6-005, M6-006

Execute the complete supported matrix from first-run setup and vanilla import through edit,
incremental bake, optimized WAD, development ISO patch, PCSX2 load, recovery, and update.

Acceptance:

- Linux and Windows pass the same documented NTSC-U UYA workflow.
- No P0 requirement remains untested, waived silently, or dependent on proprietary CI data.
- Known limitations are documented and no open issue risks clean ISO or project data.
- Release artifacts, checksums, provenance, version metadata, and update channels agree.

Verification: signed P0 qualification report linked to the stable release candidate.
