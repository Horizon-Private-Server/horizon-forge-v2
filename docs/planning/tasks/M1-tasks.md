# M1 task register — Project and asset foundation

Milestone: [M1 — Project foundation](../milestones/M1-project-foundation.md)

## M1-001 — Implement settings and application paths

Status: ✅ Complete

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

Status: ✅ Complete

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

Status: ✅ Complete

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

Status: ✅ Complete

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

Status: ✅ Complete

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

Status: ✅ Complete

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

Status: ✅ Complete

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
The idle timer, pre-transition flush, and `Cmd/Ctrl+S` binding now attach to the
long-lived authoritative editor state implemented by M2-001.

## M1-008 — Resolve missing assets and maintain the catalog safely

Status: ✅ Complete

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

## Future asset-identity follow-ups

## M1-009 — Publish global vanilla asset lookup manifests

Priority: Future
Requirements: FR-ASSET-001, FR-ASSET-003, FR-ASSET-004, FR-APP-005,
NFR-PORT-002, NFR-REL-002
Depends on: M1-003, M1-004, M5-001

Generate and check in a schema-versioned lookup file for each supported
`GameId`/asset-kind pair, initially UYA mobys, ties, shrubs, and textures. Key
entries by Forge Asset ID so the lookup remains available when a user's local
catalog entry or blob is missing.

The current UYA moby, tie, and shrub IDs cover canonical model-plus-texture bundles;
their identity must not change. Also catalog each normalized texture as an
`AssetKind.Texture` so the texture lookup uses real resolvable Asset IDs rather than
an unrelated checksum. Record all source appearances: game, supported revision,
level, model kind and oClass for model bundles, and owner kind/oClass, role, and
slot for textures. `commonName` is optional mutable documentation and is excluded
from identity.

Acceptance:

- Deterministic files exist separately for UYA `moby`, `tie`, `shrub`, and
  `texture`; their stable ordering makes regeneration reviewable.
- Each entry records Asset ID, kind, canonical-format version, and every known
  source appearance. Identical content used by several levels or oClasses remains
  one entry with several appearances rather than last-write-wins metadata.
- Existing model-bundle Asset IDs and projects remain valid while standalone
  normalized PIFs gain `AssetKind.Texture` IDs and catalog provenance.
- Texture entries use owner, role, and slot metadata instead of inventing a texture
  oClass. Placeholder textures are marked non-restorable and cannot masquerade as
  recovered retail content.
- Manifests contain no asset bytes, ISO fingerprints, user paths, or other
  machine-local data; adding another game means adding that game's generated files,
  not weakening the shared schema with guessed fields.
- Generation rejects kind/format mismatches and invalid Asset IDs, writes through
  validate-then-atomic-replace, and produces no diff from the same supported source
  and importer version.

Verification: authored deduplication and multi-appearance fixtures, texture-owner
fixtures, manifest schema/ordering tests, invalid-entry tests, and a retained local
full-UYA regeneration report with no proprietary payloads.

## M1-010 — Diagnose and restore missing assets from the global lookup

Priority: Future
Requirements: FR-ASSET-004, FR-APP-004, FR-APP-005, NFR-PORT-002,
NFR-REL-001, NFR-UX-003
Depends on: M1-008, M1-009

Use the checked-in manifests as the fallback when project inspection cannot find an
Asset ID in the project or per-user catalog. Resolve lookup data in the host and
return a game-neutral diagnostic to the renderer.

Acceptance:

- A known missing ID displays game, asset kind, oClass or texture-owner details,
  known levels, optional common name, and the canonical-format version even when
  the local catalog has no row for it.
- Diagnostics distinguish a known restorable vanilla asset, a known non-restorable
  placeholder, a project-owned asset, and an unknown ID; unknown entries retain the
  existing ID/kind report without guessed provenance.
- The repair action names the required supported game/revision and source levels,
  validates the user-selected clean ISO, imports only the required levels, and
  verifies that the expected Asset ID now resolves before reporting success.
- A registry match alone never makes an asset repairable when its canonical format
  or selected source is incompatible. Failure explains whether Forge, the source,
  or the project backup must be restored or upgraded.
- The project format remains portable and stores no duplicate lookup metadata or
  machine-specific source path.

Verification: known-model, known-texture, unknown, project-owned, placeholder,
wrong-revision, stale-canonical-version, and successful targeted-restore fixtures.

## M1-011 — Curate optional UYA asset common names

Priority: Future
Requirements: FR-ASSET-003, NFR-PORT-002
Depends on: M1-009

Add manually reviewed `commonName` values to the checked-in UYA lookup manifests as
asset documentation becomes available. Keep the work incremental: oClass, owner,
and level metadata remains the complete fallback for unnamed assets.

Acceptance:

- Names are concise, game-specific, reviewable in ordinary manifest diffs, and do
  not change Asset IDs, source appearances, repair behavior, or generated ordering.
- One Asset ID has at most one current common name per game; uncertain or conflicting
  names remain unset until resolved rather than being presented as fact.
- Regeneration preserves curated names for still-valid Asset IDs and reports names
  whose IDs disappeared after an intentional canonical-format/importer change.
- Forge always shows the oClass or texture-owner fallback alongside a common name so
  diagnostics remain unambiguous.

Verification: manifest validation plus regeneration/removed-ID preservation fixtures.
