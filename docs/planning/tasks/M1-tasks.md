# M1 task register — Project and asset foundation

Milestone: [M1 — Project foundation](../milestones/M1-project-foundation.md)

## M1-001 — Implement settings and application paths

Requirements: FR-SET-001, FR-SET-002, NFR-PORT-001, NFR-UX-002  
Depends on: M0-001, M0-005

Implement the typed flat settings registry, OS-appropriate application-data paths,
defaults, validation, unknown-key preservation, search/reset UI, and safe writes.

Acceptance:

- Settings round-trip on Linux/Windows with stable flat keys.
- Invalid values fall back with diagnostics; unknown keys survive.
- Machine paths remain outside projects.
- No secret storage mechanism is invented before secrets exist.

Verification: schema/default/unknown-key tests and packaged settings smoke.

## M1-002 — Build UYA setup and development ISO creation

Requirements: FR-APP-003, FR-APP-004, DR-003, DR-004, NFR-REL-001, NFR-UX-003  
Depends on: M1-001, M0-004

Build the first-run wizard for clean NTSC-U UYA ISO selection, identity validation,
projects/development directories, disk-space preflight, and transactional copy.

Acceptance:

- Unsupported/wrong variants are rejected with identity details.
- Development output can never resolve to the clean source or a hard link.
- Copy reports progress, cancels safely, resumes/restarts predictably, and uses
  temporary-file-and-replace.
- Source remains byte-identical through success and failure tests.

Verification: synthetic image fixtures, same-file/hard-link checks, disk-full and
cancellation simulations, and local clean-ISO manual verification.

## M1-003 — Implement the content-addressed store and catalog

Requirements: FR-ASSET-001, FR-ASSET-003, FR-ASSET-004, NFR-REL-001  
Depends on: M0-005, M1-001

Store immutable canonical blobs by Asset ID and index kind, game, revision, maps,
source archive/index, aliases, tags, provenance, and importer version.

Acceptance:

- Duplicate content stores one blob with many source appearances.
- Metadata re-index does not rewrite identity blobs.
- Queries by ID/kind/game/level/tag are deterministic and bounded.
- Interrupted insertion leaves neither a false catalog row nor partial final blob.

Verification: deduplication, many-to-many tags, transactional failure, and catalog
reopen tests using authored fixtures.

Implementation: `AssetCatalogStore` persists immutable canonical blobs beneath
two-character Asset-ID prefixes and atomically replaces a deterministic v0 JSON
catalog. Metadata-only updates preserve blobs; exact-match queries are sorted and
bounded. Authored tests cover deduplication, merged source/tag metadata, reopen,
cancellation, complete-orphan reuse, and catalog commit failure. The frozen layout
is recorded in [asset catalog v0](../../asset-catalog-v0.md).

## M1-004 — Import the global UYA asset catalog

Requirements: FR-APP-005, FR-ASSET-003, DR-002, NFR-PERF-004  
Depends on: M1-002, M1-003, M0-004

Use the .NET SDK to scan every supported UYA map for vanilla ties, shrubs, and
mobys, canonicalize them, and record all appearances once globally.

Acceptance:

- Import is progress-reporting, cancellable, and resumable by ISO fingerprint and
  importer version.
- Cross-map duplicates share Asset IDs while retaining all level tags.
- Removing the ISO does not remove completed cache data.
- Heavy work does not block Electron main/renderer.

Verification: authored/synthetic catalog fixtures plus local full-UYA import report.

Implementation: `UyaAssetImportService` scans populated UYA level-table entries
through the pinned SDK, imports deterministic model-plus-texture bundles in
per-level catalog batches, and atomically checkpoints by ISO fingerprint and
importer version. Setup drives the host operation with progress/cancellation and
resumes incomplete imports. The frozen representation is recorded in
[UYA global asset import v0](../../uya-asset-import-v0.md).
The representative clean-disc run is recorded in the
[M1-004 import report](../../m1-004-uya-import-report.md).

## M1-005 — Implement project format and isolated overrides

Requirements: FR-ASSET-002, FR-ASSET-006, FR-PROJ-002, FR-PROJ-003, NFR-PORT-001  
Depends on: M0-005, M1-003

Implement the versioned manifest/content layout, stable Entity IDs, global Asset-ID
references, provenance, target profile, and copy-on-write project overrides.

Acceptance:

- Project files contain only relative/project data and logical global IDs.
- Entity transforms do not copy resource assets.
- First resource edit creates a project asset and never changes global bytes.
- Undo can restore the global reference; **Make unique** isolates one reference.

Verification: round-trip, move-directory, copy-on-write, undo, and cross-project
isolation fixtures.

Implementation: `ForgeProjectWorkspace` persists the version-zero manifest and
content documents, validates portable relative paths, resolves project assets
before the global catalog, and stores derived immutable blobs by Asset ID. Resource
edits return reversible reference changes; shared edits redirect matching project
references while **Make unique** redirects only the selected entity. Authored tests
cover deterministic round-trip, directory moves, transform-only edits,
copy-on-write, undo cleanup eligibility, future-schema rejection, and cross-project
isolation. The frozen layout is recorded in
[Forge project format v0](../../project-format-v0.md).

## M1-006 — Build project hub and base-level creation

Requirements: FR-PROJ-001, FR-PROJ-002, FR-XLT-003, NFR-UX-003  
Depends on: M1-004, M1-005

Build recent-project management and project creation from a supported UYA base
level using global assets and host capability discovery.

Acceptance:

- Hub create/open/rename/remove-from-recents/reveal actions work.
- Base creation explains missing assets/capabilities before writing.
- Result opens with stable entities and provenance.
- Deleting/editing base content changes only the project.

Verification: project lifecycle UI tests and a local representative base-level run.

Implementation: the project hub manages a bounded recent-project list and supports
create, open, rename, remove-from-recents, and reveal actions. UYA creation lists
populated levels through the host, requires acknowledgement of capability gaps,
and creates stable moby entities with source-instance provenance. Renderable mobys
reference the global catalog; intentional model-less/controller mobys remain
editable entities without a false missing-asset warning. The pinned SDK does not
yet expose UYA tie or shrub instance
transforms, so those scene entities are explicitly reported as unavailable rather
than silently approximated; their base-WAD content remains the later bake source.
The representative disc check is recorded in the
[M1-006 UYA base-project report](../../m1-006-uya-base-project-report.md).

## M1-007 — Add safe save, migration, autosave, and recovery

Requirements: FR-PROJ-004, NFR-REL-001, NFR-REL-002, NFR-REL-003, NFR-REL-004  
Depends on: M1-005, M1-006

Implement deterministic save snapshots, atomic replacement, autosave, bounded
recovery history, migration, and newer-schema protection.

Acceptance:

- Dirty/clean state is accurate across save and reopen.
- Interrupted saves retain the last explicit save.
- Recovery is offered, previewable, and never silently replaces explicit state.
- Supported old schema migrates; newer schema is not overwritten.

Verification: kill/failure injection, deterministic serialization, migration, and
recovery-bound tests.

Implementation: `ForgeProjectWorkspace` derives dirty state from deterministic
state fingerprints and exposes separate explicit-save and recovery-snapshot paths.
Explicit saves use a two-document rollback journal; startup/open completes rollback
after an interrupted save. Autosaves are deduplicated and bounded to 10 snapshots
and 256 MiB, remain separate from the explicit save, and can be previewed and
explicitly restored from the project hub. Schema v1 adds document-kind markers;
v0 projects migrate in memory and are written only after the user accepts the
upgrade. The contract is recorded in [project format v1](../../project-format-v1.md).
The idle timer, pre-transition flush, and `Cmd/Ctrl+S` binding attach to the
long-lived authoritative editor state in M2-001; before that runtime exists,
polling the clean on-disk project would create false autosaves rather than protect
unsaved edits.

## M1-008 — Resolve missing assets and maintain the catalog safely

Requirements: FR-ASSET-004, FR-APP-004, NFR-PORT-002, NFR-UX-003  
Depends on: M1-003, M1-004, M1-006

Add missing-asset diagnostics, moved-ISO repair, incremental re-import, explicit
garbage collection, and portable-project validation.

Acceptance:

- Missing IDs show kind/provenance and a valid repair action.
- Repaired source paths are revalidated before use.
- Garbage collection previews scope and never removes known referenced blobs
  without confirmation.
- Zipped project transfer resolves against another user's matching catalog.

Verification: missing/moved source fixtures, GC dry-run/apply tests, and Linux to
Windows portability record.

Implementation: project inspection groups unresolved references by Asset ID with
kind, entity count, provenance, and a guided repair state. Repair revalidates the
clean ISO against the project's stored fingerprint and re-imports only affected
source levels. Selecting a moved copy of the same clean ISO preserves completed
setup state. Catalog maintenance scans configured/recent projects and recovery
snapshots, blocks on unreadable known projects, previews counts/bytes by kind, and
requires an exact state-derived confirmation token before deleting unreferenced
entries and blobs. Portable-project fixtures resolve after a filesystem move
against a separately populated matching catalog. The contract is recorded in
[asset repair and catalog maintenance v0](../../asset-maintenance-v0.md).
