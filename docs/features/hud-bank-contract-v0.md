# HUD bank and sprite-ID contract v0

Status: frozen for M8 implementation (2026-10-07)

This note freezes the native concepts and preservation rules used by M8-002 through
M8-005. It describes the shared HUD layout currently read by the pinned Ratchet PS2
SDK (`v0.6.4`) and the game-owned rules for assigning new UYA and Deadlocked sprite
IDs. It does not authorize a writable Deadlocked level pipeline.

## Evidence and terminology

The contract is based on the SDK's `HudBankReader`, eleven extracted UYA HUD headers
with all five bank files, five extracted Deadlocked HUD manifests, and the Deadlocked
`LoadHudBanks`, `LinkHudBank`, `Hud_GetIconIndex`, and `GetIconFrame` routines.

The native limit is **five physical HUD banks**, numbered 0 through 4. The cumulative
palette and texture counts assign records to those banks. There is no separate
per-entry page field in the header or any inspected runtime lookup, so Forge and the
SDK must use “bank,” not “page.” A bank may be present but empty.

An icon mapping connects one 16-bit sprite ID to a contiguous range in the frame
table. A frame connects one palette-table index to one texture-table index. Palettes
and textures then point into a physical bank.

## Fixed header (`0xB4` bytes)

All integers are little-endian.

| Offset | Size | Ownership | Meaning |
| --- | ---: | --- | --- |
| `0x00` | `u16` | derived | Icon-record count, including the terminal record. Mapping count is this value minus one. |
| `0x02` | `u16` | derived | Frame count. |
| `0x04` | `i32` | derived | Icon-mapping table offset. |
| `0x08` | `i32` | derived | Frame table offset. |
| `0x0C` | `i32` | derived | Palette table offset. |
| `0x10` | `i32` | derived | Texture table offset. |
| `0x14` | `5 * i32` | derived | Cumulative palette counts after banks 0 through 4. Values are non-negative and non-decreasing. |
| `0x28` | `3 * i32` | opaque | Observed as zero. Preserve; do not expose as editable state. |
| `0x34` | `5 * i32` | derived | Cumulative texture counts after banks 0 through 4. Values are non-negative and non-decreasing. |
| `0x48` | `3 * i32` | opaque | Observed as zero. Preserve; do not expose as editable state. |
| `0x54` | `5 * i32` | derived | Decompressed sizes of physical banks 0 through 4; zero means empty. |
| `0x68` | `0x4C` | runtime/opaque | Serialized runtime scratch. The loader/linker writes CPU/GS bank handles within `0x74..0xA4`; unclassified bytes remain opaque. Extracted files contain zeroes. |

Runtime scratch is not project state. A no-edit operation preserves the source bytes.
A newly composed header writes zeroes in this region; it never serializes live process
pointers or stash handles.

Any bytes between the end of a variable table and the next declared table offset are
alignment/padding bytes and remain opaque. They are preserved unless the writer must
relayout that exact region, in which case it emits deterministic zero padding.

## Variable tables

### Icon mappings

There are `header count - 1` eight-byte mappings followed by one four-byte terminator.

| Relative offset | Type | Meaning |
| --- | --- | --- |
| `+0x00` | `u16` | Sprite ID. Unique across all mappings. |
| `+0x02` | `u16` | Number of animation frames. |
| `+0x04` | `u16` | First frame-table index. |
| `+0x06` | `u16` | Opaque padding, observed as zero and preserved. |

The terminal value is exactly `0x0000FFFF`. Runtime lookup is sentinel-driven and
supports at most 1,024 mappings. Every mapping must satisfy
`firstFrameIndex + frameCount <= frameCountInHeader` without overflow.

### Frames

Each four-byte frame is `(i16 paletteIndex, i16 textureIndex)`. Negative indexes are
not authorable. Consequently an editable table is capped at 32,768 addressable
palette or texture records even though cumulative counts are stored as `i32`.

### Palettes

Each eight-byte palette record is `(u32 encodedOffset, u16 gsRam, u16 padding)`.
Serialized offsets carry the high bit; the bank-relative byte offset is
`encodedOffset & 0x7FFFFFFF`. Every decoded palette is exactly `0x400` bytes: 256
RGBA entries for the indexed-8 HUD texture path. `gsRam` is runtime-derived when zero;
padding is opaque and preserved.

### Textures

Each eight-byte texture record is
`(u32 encodedOffset, u16 gsRam, u8 widthLog2, u8 heightLog2)`. Width and height are
`1 << log2`; pixel length is `width * height` indexed-8 bytes. The decoded range must
fit its selected decompressed bank and all size arithmetic is checked before
allocation.

### Physical-bank routing

For record index `i`, the selected bank is the first cumulative count greater than
`i`. Counts may stay equal across empty banks. The final cumulative count is the table
length. This routing is the only native “five page” concept found by M8-001.

### Bank byte ownership

Within each decompressed bank, the `0x400`-byte ranges named by palette records and
the `width * height` ranges named by texture records are typed payload. Existing gaps,
alignment bytes, unreferenced ranges, and trailing bytes are explicitly opaque. A
no-edit rebuild preserves every bank byte. A changed rebuild may replace an edited
typed range or consume deterministic padding for an append, but it does not normalize
or discard any other byte. The WAD container representation is likewise opaque on the
no-edit path and codec-owned only when a changed bank must be recompressed.

## Compression and alignment

Each bank archive slot may contain either a WAD-compressed payload or raw bytes. The
header's bank size is the decompressed size. SDK parsing operates on decompressed
banks; archive orchestration owns compression detection and decompression.

Deadlocked runtime code aligns a loaded CPU bank base to `0x10` and rounds load/DMA
sizes to `0x40`. Existing palette offsets are `0x100` aligned and inspected texture
offsets are at least `0x40` aligned. M8-002 therefore preserves every existing offset
and uses `0x100` palette and `0x40` texture alignment for appended payloads. These are
writer policies; the reader validates bounds and does not reinterpret padding.

## Sprite-ID text and assignment rules

The native lookup compares the full `u16`; it does not enforce a prefix. Prefix rules
are game-owned namespaces for **new** mappings:

| Game | New-ID range | Canonical text |
| --- | --- | --- |
| UYA | `0xE000..0xEFFF` | `EXXX` |
| Deadlocked | `0x7500..0x75FF` | `75XX` |

Parsing accepts exactly four hexadecimal digits, case-insensitively, after trimming
outer whitespace. It does not accept a `0x` prefix. Formatting always emits four
uppercase digits. `0xFFFF` is reserved for the icon-table terminator. A syntactically
valid new ID is unavailable when any imported or custom mapping already owns the same
full 16-bit value; uniqueness is global to the HUD mapping table.

Existing mappings outside a game's new-ID namespace remain readable and must be
preserved. This matters in practice: the UYA corpus contains `0000` and several
`75xx` mappings alongside its `Exxx` mappings. Forge must not “correct,” hide, or
renumber them.

## Frozen rebuild behavior

M8-002 owns the only HUD writer. Its contract is:

- With no semantic edits, return the original header and each original bank byte for
  byte, including the original compressed/raw representation, opaque fields, padding,
  table order, indexes, and runtime-scratch bytes.
- Replacement keeps the existing sprite ID, mapping index, and frame index. The edited
  frame is remapped to isolated palette and texture records appended in the last
  populated bank, while every untouched frame retains its original indexes. This
  prevents shared source records from leaking an edit into another frame. Append never
  renumbers an existing record: the caller selects the bank, additions are ordered at
  or after the last populated bank, and payloads are placed deterministically at the
  aligned bank end.
- A changed result is accepted only after a semantic re-read proves mappings, frame
  ranges, bank routing, dimensions, payload bytes, sizes, and unique IDs.
- Validation and composition complete before archive replacement. Failure or
  cancellation yields no successful output and leaves staging unchanged.

Unsupported inputs fail with an entry/bank diagnostic: missing or invalid terminator,
more than 1,024 icon mappings, decreasing/negative cumulative counts, duplicate sprite
IDs, negative or out-of-range frame handles, non-power-of-two or non-indexed-8 custom
textures, palette sizes other than `0x400`, decoded ranges outside a bank, sizes that
overflow supported integer ranges, and custom IDs outside the target game's namespace.
Unknown header fields, runtime scratch, palette/texture `gsRam`, and opaque gaps are
not directly editable. Forge does not insert a sixth bank, invent logical pages, or
enable Deadlocked writing as part of M8.

## Verification anchors

- The SDK HUD contract fixture reads a synthetic palette and texture from each of all
  five physical banks and rejects a missing terminator.
- The UYA and Deadlocked game libraries own boundary, parse, format, terminator,
  occupied-ID, and availability fixtures; the renderer contains no prefix conditionals.
- The pinned extraction corpus establishes five physical UYA bank files, zero-capacity
  banks, WAD-compressed and raw paths, and imported IDs outside the new-ID namespaces.
- The M8-002 SDK suite proves exact no-edit header/stored-bank identity across every
  available UYA extraction, then covers compressed/raw replacement, stable append,
  deterministic output, semantic re-read, malformed/capacity diagnostics,
  cancellation, and a bounded 4 MiB bank.
