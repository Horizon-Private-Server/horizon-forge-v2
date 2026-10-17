# UYA custom TIE class contract v1

Status: frozen for M14 implementation  
Task: M14-001  
Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)

## Identity and namespace

A custom TIE's canonical `AssetId` is its stable project identity. Its native class ID
is target metadata and never participates in the content hash. Project schema 20 stores
the mapping in `customClasses` using registry schema 1 and allocation version 1.

The UYA adapter owns `0x9000` through `0xFFFF`, inclusive, for project custom TIEs.
That gives 28,672 slots. All four class-bearing TIE structures use at least 16 bits;
the existing static-asset composer accepts `0x0000` through `0xFFFF`. TIE tables are
counted, so `0xFFFF` is an addressable class ID rather than a sentinel. Texture indexes
use separate `0xFF` sentinels and are not class IDs.

## Native rewrite surface

Every custom class must agree in these locations after M14-003/M14-006 composition:

| Structure | Location | Encoding | Required value |
| --- | --- | --- | --- |
| TIE model payload | TIE header `OClass` at `+0x44` | signed little-endian 16-bit, interpreted as unsigned bits | Allocated class ID |
| Asset header TIE definition | table at header `+0x24`, `0x20`-byte entries, class at entry `+0x04` | little-endian 32-bit | Allocated class ID |
| Gameplay TIE class list | block pointer at gameplay-core header `+0x30`; count at `+0x00`, classes from `+0x04` | counted little-endian 32-bit list | Sorted distinct set of defined classes |
| Gameplay TIE instance | block pointer at gameplay-core header `+0x34`; `0x10`-byte table header, `0x60`-byte records, class at record `+0x00` | little-endian 32-bit | Allocated class ID |

TIE groups store instance indexes, occlusion stores instance/visibility identifiers,
and ambient lighting is ordered by instance. They must be regenerated or reordered
with instances, but they do not contain another TIE class ID. Instanced collision uses
project Asset/Entity IDs and likewise adds no native class-ID rewrite site.

## Project registry

Each allocation contains:

- exact TIE `AssetId` and `AssetKind`;
- target class ID;
- origin (`ImportedModel` or `DonorMap`); and
- the donor source class ID when the origin is `DonorMap`.

The registry rejects unsupported schema/allocation versions, missing or wrong-kind
attached assets, duplicate Asset IDs, and duplicate `(kind, class ID)` targets. UYA
additionally rejects non-TIE assets and IDs outside `0x9000`–`0xFFFF`. Registry assets
are live project references, including in recovery snapshots, so ordinary cleanup
cannot collect them.

## Allocation and donor remap rules

Allocation is deterministic and atomic:

1. Reuse the existing target when the same `AssetId` is already allocated.
2. For a donor asset, preserve its source slot when it is inside the custom range and
   is free in both the base/native inventory and project registry.
3. Otherwise choose the lowest free ID beginning at `0x9000`.
4. Process donor assets by `AssetId`, then source class ID, so input order cannot change
   the result.
5. Multiple donor source IDs with identical content share one target allocation and
   each receives an explicit remap. One donor source ID naming different Asset IDs is
   invalid.
6. Exhaustion fails before the caller replaces project state. There is no eviction,
   wraparound, or implicit reuse of a live slot.

Deletion does not make a class ID available until the allocation and every referencing
entity/history/recovery record are removed by the owning M14-004 transaction. Reimport
with changed canonical content receives a new Asset ID and therefore a distinct slot;
explicit dependent retargeting and old-blob retention remain M14-004 work.

## Retail corpus evidence

The checked-in [inventory report](../qualification/uya-custom-tie-class-inventory.json)
covers the 45 UYA NTSC-U 1.00 level WADs present in the supported corpus. The corpus
identity is SHA-256
`0102753e626e1d6e8e17f8d2a2e2d954932e6853a62eff7075bd5e99ab34bfc9`, computed over
the concatenated binary SHA-256 digests of level WADs sorted by filename.

Across 1,662 distinct TIE classes, the observed range is `0x01C8`–`0x1FFE`; no class
uses `0x9000`–`0xFFFF`. For every level, the asset-definition set equals the gameplay
class list, every instance class has a definition, and every embedded TIE `OClass`
matches its asset definition. The report contains hashes and counts only, never retail
bytes or local paths.

## P1 ownership reconciliation

M14 is the single completion path for the unfinished project-local model work:

| Historical task | M14 owner |
| --- | --- |
| P1-001 project-local custom asset lifecycle | M7-002 foundation plus M14-004 model transaction |
| P1-002 inspect/canonicalize GLB | M14-002 |
| P1-005 convert GLB to native models | M14-003 and M14-006 for the constrained TIE slice |
| P1-006 preview and end-to-end qualification | M14-005 and M14-007 |

P1-003/P1-004 remain completed texture/palette dependencies. Shrubs, mobys, skins, and
animation stay deferred and do not create parallel implementations during M14.
