# UYA moby instance contract v0

Status: Frozen for M9-001; typed property contract implemented by M9-002  
Target: UYA NTSC-U 1.00  
Owners: `RatchetPs2.Games.UYA.Gameplay.UyaMobyInstancesReader` and
`UyaMobyInstancesWriter`

This contract freezes the native UYA moby-instance block before Forge exposes
field editing. It describes source bytes and game semantics; renderer code must
consume later game-neutral descriptors and must never encode these offsets.

## Evidence and confidence

- The SDK reads the block from `gameplay/core/moby_instances.bin` and exercises a
  synthetic representative record through the UYA gameplay reader and writer.
- The 0x88 layout agrees with the independently reviewed `GcUyaMobyInstance`
  layout used by Wrench for GC/UYA, including the 0x20 and 0x40 sentinels and
  `0 = precompute` occlusion note.
- Forge already round-trips class, transform, and scale through this writer while
  retaining the source record as the template.
- Names beginning with `unknown` are positional observations, not inferred
  semantics. They remain opaque until new evidence is reviewed.

The synthetic fixtures contain no game data. Extracted levels may be used for
local qualification, but are not committed.

## Block layout

All values are little-endian. The block is a 0x10-byte header, exactly
`staticCount` 0x88-byte records, then opaque trailing bytes.

| Offset | Size | Meaning | Ownership |
| --- | ---: | --- | --- |
| `0x00` | 4 | Static record count | Derived from the output record list |
| `0x04` | 4 | Spawnable/dynamic moby capacity | Typed, preserved by v0 writer |
| `0x08` | 4 | Header padding/unknown | Opaque, preserved |
| `0x0C` | 4 | Header padding/unknown | Opaque, preserved |
| `0x10...` | `count × 0x88` | Static moby records | See below |
| trailing | variable | Unowned source tail | Opaque, preserved byte-for-byte |

Counts are non-negative and bounded by the supplied payload. Checked arithmetic
must reject a count whose records do not fit; `TryRead` returns `false` for an
incomplete table rather than throwing.

## Record layout

The “field key” column reserves stable adapter keys for M9-002. It does not expose
the native layout to Forge or promise that every typed value is editable.

| Offset | Size | Native value | Classification | Stable field key | v0 write behavior |
| --- | ---: | --- | --- | --- | --- |
| `0x00` | 4 | Record size, exactly `0x88` | Derived | — | Rewritten as `0x88` on transform edits |
| `0x04` | 4 | Mission | Typed integer; exact domain not yet proven | `instance.mission` | Editable from `-1` through `127` |
| `0x08` | 4 | Unknown 08 | Opaque | — | Preserved |
| `0x0C` | 4 | Unknown 0C | Opaque | — | Preserved |
| `0x10` | 4 | UID | Typed identifier with external consumers | `instance.uid` | Preserved/read-only |
| `0x14` | 4 | Bolts | Typed integer | `instance.bolts` | Editable, non-negative |
| `0x18` | 4 | Unknown 18 | Opaque | — | Preserved |
| `0x1C` | 4 | Unknown 1C | Opaque | — | Preserved |
| `0x20` | 4 | Unknown 20 | Opaque | — | Preserved |
| `0x24` | 4 | Unknown 24 | Opaque | — | Preserved |
| `0x28` | 4 | OClass | Typed non-negative class ID | `instance.classId` | Asset-owned; visible and read-only in the property contract |
| `0x2C` | 4 | Uniform scale | Finite non-zero float | `transform.scale` | Editable; uniform project scale only |
| `0x30` | 4 | Draw distance | Typed integer, game units | `instance.drawDistance` | Editable, non-negative |
| `0x34` | 4 | Update distance | Typed integer, game units | `instance.updateDistance` | Editable, non-negative |
| `0x38` | 4 | Retail sentinel `0x20` | Derived for new records | — | Existing value preserved |
| `0x3C` | 4 | Retail sentinel `0x40` | Derived for new records | — | Existing value preserved |
| `0x40` | 12 | Position XYZ | Three finite floats, game world units | `transform.position` | Editable |
| `0x4C` | 12 | Rotation XYZ | Three finite radians, native ZYX Euler order | `transform.rotation` | Editable through normalized quaternion conversion |
| `0x58` | 4 | Moby-group index, `-1` for none | Derived native index | `instance.group` | Preserved until group-table ownership exists |
| `0x5C` | 4 | Rooted state | Boolean encoded as native integer | `instance.isRooted` | Editable; writes canonical `0` or `1` |
| `0x60` | 4 | Rooted distance | Typed float, game world units | `instance.rootedDistance` | Editable; finite `-1` sentinel or non-negative value |
| `0x64` | 4 | Unknown 64 | Opaque | — | Preserved |
| `0x68` | 4 | PVar index, `-1` for none | Derived native index | `instance.pvar` | Preserved until PVar ownership exists |
| `0x6C` | 4 | Occlusion mode/index; `0` requests precompute | Typed integer with incomplete enum semantics | `instance.occlusion` | Preserved/read-only |
| `0x70` | 4 | Mode bits | Typed flags with unknown bit meanings | `instance.modeBits` | Preserved/read-only |
| `0x74` | 12 | Light color RGB | Three native integer channels, nominally 0–255 | `instance.color` | Editable; each channel is `0...255` |
| `0x80` | 4 | Directional-light index | Typed native reference | `instance.light` | Preserved until reference resolution exists |
| `0x84` | 4 | Unknown 84 | Opaque | — | Preserved |

The transform writer owns OClass and transform bytes. The typed property writer owns
only the explicitly editable ranges above. Both copy the 0x88-byte source template
first and patch their owned ranges, so every other byte stays intact. The no-edit
writer copies every retained raw record and the opaque tail exactly. Opaque fields
cannot be promoted by appearance or observed value alone.

## Validation and limits

- Header and record range arithmetic is checked before allocation.
- Every parsed record must advertise size `0x88`.
- No-edit writes require exactly 0x88 retained raw bytes per record.
- Transform edits require finite position, quaternion, and scale values; scale
  cannot be zero and the quaternion cannot be empty.
- OClass must be non-negative.
- New records use the SDK template defaults: scale 1, draw/update distance 1024,
  sentinels 0x20/0x40, group/PVar `-1`, and neutral RGB.
- UID uniqueness and native group/PVar/light index resolution are project-level
  concerns and are not guessed by the record writer.
- Property edits require the expected OClass to match both project metadata and the
  semantic re-read, reject unknown/read-only keys, and accept exactly one typed value.

## Cross-game boundary

The byte layout, native field enum, validation, and writer remain in
`RatchetPs2.Games.UYA`. The UYA Forge adapter owns the stable keys and projects parsed
values into game-neutral Domain and bridge descriptors containing labels, value kinds,
validation metadata, and typed values. Shared Domain and renderer code receive no UYA
offsets or UYA record types. A future DL adapter supplies its own 0x70-byte layout
behind the same neutral bridge contract rather than sharing this binary implementation.

## Qualification fixtures

The focused `--uya-static-instances` suite verifies:

- the representative record maps every typed offset expected by the reader;
- retained raw record and trailing bytes are exact;
- representative and boundary no-edit writes are byte-identical;
- truncated and impossible-count `TryRead` calls fail without allocation;
- invalid record-size markers and short retained records are rejected; and
- transform and every supported property edit change only their owned bytes;
- per-field minimum/maximum values round-trip through semantic re-read; and
- unknown keys, read-only fields, wrong value kinds, non-finite floats, invalid
  colors, and stale OClass expectations are rejected before mutation.
