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

