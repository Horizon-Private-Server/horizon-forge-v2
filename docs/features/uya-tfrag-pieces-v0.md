# UYA tfrag piece contract v0

Status: M12-001 through M12-007 implemented; packaged cross-platform and PCSX2
qualification remains under M12-008 (2026-10-11)

This note fixes the native ownership boundary and initial transform policy used by
M12-002 onward. Binary parsing, canonical extraction, and composition remain SDK
responsibilities.

## Native editable unit

One editable piece is one `0x40`-byte record in a terrain payload's native tfrag table,
together with the record's data range and all three LODs. In current SDK code this is
named `TfragChunk`; “chunk” there means a table record, not a level chunk WAD.

This is the smallest source-native unit with independent bounds and a contiguous owned
data range. A renderer primitive is too small: material primitives are derived by
decoding draw controls and topology packets that share position uploads, vertex
references, color/light data, bounds, and LOD streams. Forge must therefore pick and
transform the native record as one piece even when it renders as several primitives.

## Terrain header and group table

All fields are little-endian. Every inspected UYA payload places the table at `0x40`.

| Offset | Type | Meaning and ownership |
| --- | --- | --- |
| `0x00` | `i32` | Group-table offset. Must be at or after the `0x40` header. |
| `0x04` | `i32` | Number of `0x40` group records in this payload. |
| `0x08` | `f32` | Terrain/grouping radius. Preserve until writer semantics are proven. |
| `0x0c` | `i32` | Aggregate tfrag count. It can exceed the local table count when streamed terrain exists. |
| `0x10..0x3f` | bytes | Unclassified header data; source-owned and preserved. |

The reader bounds the record count against the payload before allocation. Record data
offsets are relative to the table base, strictly increasing, inside the payload, and
`0x10` aligned. The next record's data offset terminates a record. The containing asset
or chunk slot terminates the last record; a coarse asset slice with unrelated trailing
bytes is not an exact piece boundary.

## `0x40` group record

| Offset | Size | Classification |
| --- | ---: | --- |
| `0x00` | `4 * f32` | World-space bounding sphere `(x, y, z, radius)`. |
| `0x10` | `i32` | Data offset relative to the table base. |
| `0x14..0x1f` | `6 * u16` | LOD2, shared, LOD1, LOD0, texture-table, and RGBA offsets within group data. |
| `0x20..0x23` | `4 * u8` | Common, LOD2, LOD1, and LOD0 qword counts. |
| `0x24..0x26` | `3 * u8` | Per-LOD RGBA counts. |
| `0x27` | `u8` | Base-only flag. |
| `0x28` | `u8` | Texture-entry count. |
| `0x29` | `u8` | RGBA size; the decoded table has `value * 4` RGBA entries. |
| `0x2a..0x2d` | bytes | RGBA vertex location, occlusion stash, mesh-sphere count, and flags. |
| `0x2e..0x33` | `3 * i16` | Mesh-sphere, light, and light-vertex-start offsets. |
| `0x34..0x37` | mixed | Directional-light selectors/state and point-light value. |
| `0x38..0x3b` | `2 * i16` | Cube offset and occlusion index. |
| `0x3c..0x3d` | `2 * u8` | Source vertex and triangle summary counts. |
| `0x3e` | `i16` | Mip distance. |

Fields with names but incomplete runtime semantics are parsed for diagnostics and
preserved, not normalized. The writer must derive or deliberately preserve the sphere,
mesh-sphere/cube/light fields, occlusion values, counts, flags, and mip distance; it may
not copy stale transform-dependent metadata after an edit.

## Group data

- Runtime geometry is stored as VIF command streams. Position uploads use local signed
  16-bit rows plus VIF base rows where present; preview conversion applies the existing
  PS2-to-glTF basis and a `1 / 1024` position scale.
- LOD2 scans `[lod2, shared)`. Shared/common data is `[shared, lod1)`. LOD1 topology is
  `[lod1, lod0)`. LOD0/1 shared data begins at `lod0`, while the LOD0 topology ends at
  the RGBA table. These ranges share uploads and must stay in one piece.
- Vertex references and draw-control strips produce indexes and material ranges during
  decode. A glTF primitive is therefore a projection, not native ownership.
- Each texture entry is `0x50` bytes. The verified fields are texture ID at `+0x00` and
  U/V clamp state at `+0x20/+0x24`; remaining bytes are source-owned until classified.
  The preview material key is `(texture ID, clamp U, clamp V)`.
- The RGBA table stores four-byte colors. Lighting selectors, base colors, normals, and
  post-scales decoded for preview remain part of the owning piece.

Offsets into group data are unsigned 16-bit; qword and table counts are unsigned bytes;
mesh/light/cube offsets are signed 16-bit. A future writer must preflight every encoded
range and count before allocation, then re-read the result. Existing unknown packet
commands, unclassified record bytes, padding, and payload-tail bytes stay opaque.

## Primary/chunk ownership and links

Primary terrain is the asset-WAD payload selected by the level asset header's terrain
offset. Streamed terrain is the terrain slot in `chunk1.wad` and later chunk WADs.
`chunk0.wad` is not a separate streamed owner in Forge; local extractions show it can
alias a coarse primary asset slice and include unrelated trailing bytes.

Stable occurrence provenance is:

```text
(source archive path, primary-or-chunk source index, native group index)
```

That tuple produces a distinct project Entity ID for every occurrence. The Asset ID in
M12-002 will instead hash canonical piece geometry and material dependencies, allowing
identical content to share one asset without merging occurrences or ownership.
Neither the raw record offset nor a hash of the source data slice is an Asset ID.

The record exposes an occlusion index/stash and the UYA gameplay payload has an ordered
tfrag occlusion-mapping table. Their complete rewrite relationship is not yet proven,
so M12-002 must carry them as source provenance and M12-004 must either update them
deterministically or block the edit. No cross-record geometry pointers have been
identified; VIF vertex references are local to the owning record's runtime streams.

## Transform policy

The first writable contract supports finite translation, finite rotation, and a finite,
strictly positive uniform scale. The writer rebuilds positions, VIF bases, normals,
winding as needed, bounding spheres, transform-dependent group metadata, and encoded
counts from canonical data.

Non-uniform and mirrored scale are deliberately unsupported for the first writer:
non-uniform scale invalidates spherical and lighting assumptions, while mirrored scale
requires a proven native strip-winding rewrite. Zero scale and non-finite components
are always invalid. Preflight diagnostics use these stable reasons:

- `TFRAG_TRANSFORM_NON_FINITE`
- `TFRAG_TRANSFORM_ZERO_SCALE`
- `TFRAG_TRANSFORM_NON_UNIFORM_UNSUPPORTED`
- `TFRAG_TRANSFORM_MIRRORED_UNSUPPORTED`

Forge must reject these before staging changes and identify the occurrence Entity ID
plus source owner/group. This policy can broaden only with writer fixtures and a real-
hardware result; project data must not imply support before then.

## Identity and inventory evidence

The SDK synthetic fixture contains two table records with separate indexes, record
offsets, and data offsets but byte-identical data ranges. It proves that one content
identity can have multiple occurrence identities without committing proprietary data.
The same fixture proves byte-identical source retention and rejects table-overlapping,
repeated, unaligned, and allocation-sized offsets/counts.

The local UYA inventory on 2026-10-10 found:

- 10 exact-boundary payloads: 6 primary and 4 streamed chunks;
- 3,354 native groups across those payloads;
- a largest exact payload of 1,901,584 bytes and 575 groups (`level04`, `chunk1`);
- a largest exact group data range of 4,208 bytes;
- no duplicate raw group-data hashes in the exact corpus; and
- 11 additional coarse extracted primary headers, with a largest observed local table
  of 2,876 groups (`level08`). Their trailing bytes are excluded from size/hash claims.

Observed exact records use `0x10`-aligned data starts. In the inspected exact corpus,
texture-entry count is at most 12, RGBA-size byte at most 23, vertex-count byte at most
92, and triangle-count byte at most 128. These are observations, not writer caps; the
encoded field widths and verified payload bounds are the enforceable limits.

## Canonical extraction contract

`RatchetPs2.Sdk.TfragPieceConverter` accepts bounded, exact terrain payloads with a
project-relative source archive, primary/chunk owner kind, and source index. It emits:

- one occurrence for every native group, ordered by owner kind, source index, archive,
  then native group order;
- a deterministic occurrence key plus normalized archive, owner/group, and occlusion
  provenance;
- an initial world-space PS2-basis transform whose position is the native bounding-
  sphere center; and
- translation-neutral canonical assets deduplicated by SHA-256 content ID.

The content ID is the pre-catalog deduplication key. Forge passes the same canonical
bytes and format version to Forge's typed asset identity envelope and assigns a
persistent project Entity ID to each occurrence; the SDK does not manufacture Forge
UUIDs or duplicate Forge's asset-kind header.

Canonical format version 1 is explicitly little-endian. It contains local bounds,
sorted texture dependencies, three ordered LODs, material `(texture, clamp U, clamp V)`,
triangle indices, and the decoded position/normal/UV/color/light vertex attributes
needed by rendering and a future transform writer. Floats are finite, normalized to
six decimal places, and negative zero is removed before hashing. Native offsets,
packets, record padding, and unknown fields are deliberately absent and remain SDK-
owned through the source archive/provenance path. The codec rejects invalid/trailing
data and caps one canonical piece at 64 MiB before allocation.

The optional largest complete local-level profile (`level04` primary plus chunk 1/2)
currently extracts 1,006 occurrences into 1,005 assets and 18,916,476 canonical bytes
in about 0.95 seconds, allocating about 624 MiB cumulatively under a 1.5 GiB managed-
heap cap. This is a regression baseline, not a compatibility guarantee.

The inventory is emitted by `RatchetPs2.TfragTests` when local fixtures are available.
Only counts and paths appear in output; proprietary payloads and hashes are not added to
the repository.

## Project import contract

Project schema version 19 stores one `tfrags` entity per source occurrence. The entity
owns its persistent UUID and editable transform; its attached `Tfrag` asset owns the
immutable canonical geometry. Project metadata retains the deterministic occurrence
key, source archive/kind/index, native group, occlusion values, and sorted texture IDs.
An import inventory records every source and group count, including valid empty terrain,
so reopening cannot confuse “already imported” with “migration required.”

Canonical blobs use the existing project attachment hierarchy and relative resolution,
so a moved or zipped project remains self-contained. Existing projects hydrate only
when their base-layer manifest and catalog entry prove the original game, revision,
level, fingerprint, archive, and source index. Otherwise Forge retains render-package
terrain and reports that verified source data is required. Import is atomic at the
project-content boundary; incomplete entities are never committed.

Editor snapshots expose the source owner/group metadata, local bounds, material IDs,
and decode diagnostics used by entity-aware terrain projection. Domain validation
still rejects delete, duplicate, disable, and layer changes. Transform storage remains
separate from canonical geometry; Forge exposes translation, rotation, and positive
uniform scale while the host and SDK independently enforce the frozen policy.

## Native composition and no-edit rule

`TfragTerrainReader` retains the complete supplied payload in `SourceBytes`; the
synthetic no-edit fixture verifies exact equality. The M12-007 composer starts from
those bytes and patches only verified transform-bearing fields in each changed native
group: VIF position rows and bases, light normals, record and mesh bounding spheres,
and the local bounding cube. It preserves table layout, packet order, unknown commands,
unclassified record fields, padding, and payload tails. An unchanged owner payload is
passed through byte-for-byte, and a one-piece edit leaves all later native records and
their data byte-identical.

Composition validates canonical asset/source-group identity before writing, checks
every encoded range and numeric storage limit, then re-reads the native result and
compares geometry, materials, stored vertex semantics, lighting attributes, and bounds.
Canonical vertex identity includes regenerated surface normals, which can change after
native position quantization at small scales; validation therefore compares stored
semantics by value rather than requiring the same derived vertex indexes. The
project bake fingerprint includes the composer version, all piece assets, provenance,
source positions, and transforms. Primary and streamed owners stage independently;
preflight failures identify the Entity ID and preserve the last known-good staging.
Each 16-byte mesh-sphere entry stores three world-space `f32` center coordinates, a
`u16` radius at `+0x0c`, and separate packed LOD/culling metadata at `+0x0e`. Transform
composition changes the center and radius while preserving the packed field exactly.
The bounding cube contains eight 8-byte rows whose signed XYZ components are
world-space coordinates quantized in 64-native-unit steps; the fourth component is
preserved. Composition transforms each decoded world point around the source sphere
center and requantizes it around the target center.

The SDK exposes one canonical asset per distinct decoded record and one provenance-
bearing occurrence per owner/group. Forge turns retained browser group nodes into local
canonical-asset templates and batches unique pieces by shared material with per-instance
Entity-ID picking. Validated entity transforms preview without rewriting those immutable
assets; SDK transform math supplies deterministic native-space positions, normals,
winding, and bounds to the native composer. Tfrag collision stays independent because
the retail collision groupings observed so far are substantially coarser than editable
tfrag groups and do not provide a deterministic one-to-one ownership relation.

## Generated collision

Forge does not infer links from retail collision to individual tfrag groups. A piece
may instead own an explicit individual or shared instanced-collision recipe generated
from its attached canonical local mesh. Exact modes retain the selected source LOD's
triangle topology; separate constrained-decimation modes reduce faces without merging
disconnected surfaces or bridging source holes. Recipes retain the source LOD, raw
collision type, target ratio, and generator version so regeneration is deterministic.

The existing collision composer applies the authoritative tfrag transform, preserves
one-sided winding, and validates native octant and size limits before staging. Removing
or disabling generated collision leaves independently authored retail collision
unchanged.
