# FX texture inventory and label contract v0

Status: implemented through M8-007 (2026-10-08)

This contract records the M8-006 read boundary shared by the Ratchet PS2 SDK and
Forge. It covers UYA and Deadlocked display/inventory behavior without enabling a
writable Deadlocked project pipeline.

## Native inventory

The level asset header stores the FX texture count at `0x58`, the offset of its
definition table at `0x5C`, and the FX data-region base at `0x68`. Each ordered
definition is `0x10` bytes:

| Offset | Type | Meaning |
| --- | --- | --- |
| `+0x00` | `i32` | Palette offset relative to the FX data base. |
| `+0x04` | `i32` | Indexed-pixel offset relative to the FX data base. |
| `+0x08` | `i32` | Width. |
| `+0x0C` | `i32` | Height. |

Indexes are source identity and remain ordered. A valid entry has positive
power-of-two dimensions no greater than 4,096, a `0x100`-aligned palette offset, a
`0x10`-aligned pixel offset, one `0x400`-byte RGBA32 palette, and `width * height`
indexed-8 pixel bytes within the decompressed asset payload. UYA source pixels are
unswizzled; Deadlocked source pixels are swizzled.

The SDK accepts raw or WAD-compressed asset payloads with configurable stored and
decompressed limits. It caps the table at 4,096 definitions and checks all table,
offset, dimension, length, and allocation arithmetic. Structural header failures
reject the inventory. A malformed individual definition remains in its original
position with an index-specific diagnostic and no canonical preview bytes.

Valid entries expose a canonical PIF preview. Forge computes the exact Texture Asset
ID from those bytes using canonical texture format version 1; vanilla preview bytes
are not copied into project storage merely to retain inventory state.

## Labels

UYA and Deadlocked labels live in their respective SDK game libraries. Their ordered
catalogs match the reviewed map-o-matic source for indexes `0..123`. Unknown indexes
format as `FX_TEXTURE_<index>`.

The shared negative runtime aliases are:

| Index | Label |
| ---: | --- |
| `-8` | `FX_BACK_ALPHA_CLUT` |
| `-7` | `FX_RAW_FRONT_BUFFER` |
| `-6` | `FX_RAW_BACK_BUFFER` |
| `-5` | `FX_RAW_Z_BUFFER` |
| `-4` | `FX_BACK_BUFFER_RECOPY64` |
| `-3` | `FX_BACK_BUFFER_COPY64` |
| `-2` | `FX_BACK_BUFFER_RECOPY` |
| `-1` | `FX_BACK_BUFFER_COPY` |

## Forge boundary

The bridge inventory payload is game-neutral. It carries the game, adapter
capabilities, stable index, label, dimensions, formats, offsets/lengths, swizzle and
validity state, exact source Texture Asset ID when valid, and an optional diagnostic.
Neither Electron nor React owns UYA/DL label tables or archive parsing.

UYA advertises replace and append capabilities. Deadlocked remains read-only with an
actionable reason until it has a writable project and bake pipeline.

## Authoring and composition

UYA replacement keeps the source index and appends private aligned palette and pixel
blocks at the end of the native FX segment. The UYA composer finds the next known
asset payload boundary, expands only the FX segment, and relocates every downstream
header reference atomically, so runtime FX offsets remain in the region the game
loads. It rewrites only that index's definition, so source data shared by multiple
definitions is not mutated accidentally. An exact source Texture Asset ID override
applies to every source index with that identity.

New textures append deterministic trailing definitions. Existing definitions are
never inserted, removed, or reordered. Forge therefore permits removing only the
last project addition; removing an earlier addition would shift later opaque runtime
indexes.

The SDK preserves raw versus WAD-compressed storage, validates count, dimensions,
palette/pixel format, alignment, offsets, and configured sizes, then semantically
re-reads the complete result. Bytes before the expanded FX segment and relocated
downstream payloads remain byte-identical, and unmodified definitions remain
byte-identical.

Forge copies imported images into the project's content-addressed asset hierarchy,
stores only Asset IDs and project-relative resources, fingerprints FX separately,
and stages immutable canonical PIFs. Packing applies the staged FX edits after other
asset-WAD composition so terrain/static changes and FX changes coexist.

## Verification anchors

- SHA-256 snapshots cover all 124 reviewed UYA labels and all 124 reviewed Deadlocked
  labels; focused assertions cover shared negative and unknown fallback labels.
- Synthetic raw/compressed inventories cover UYA and Deadlocked swizzle behavior,
  canonical PIFs, malformed entries, table/count/size bounds, and cancellation.
- Every available extracted UYA asset fixture retains its declared FX count and
  ordered indexes with all entries validated.
- Forge bridge round trips preserve neutral metadata and derive source Texture Asset
  IDs only for valid canonical entries.
- Writer tests cover no-edit pass-through, private replacement storage, model-heap
  relocation, trailing append indexes, compressed payloads, malformed edits,
  cancellation, and semantic re-read.
- Forge tests cover project-local replacement/addition blobs, undo/save dirtiness,
  stable-index removal rules, ZIP transfer, staging, and SDK composition.
