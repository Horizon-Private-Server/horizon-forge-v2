# Identity and project schema v0

These byte and JSON contracts are frozen for Forge's version-zero fixtures.

## Asset identity

An Asset ID is the lowercase hexadecimal SHA-256 digest of this preimage:

| Offset | Size | Encoding | Field |
| ---: | ---: | --- | --- |
| 0 | 4 | ASCII | `HFAS` domain magic |
| 4 | 2 | unsigned little-endian | identity layout version (`0`) |
| 6 | 2 | unsigned little-endian | asset-kind code |
| 8 | 4 | unsigned little-endian | canonical-format version |
| 12 | remaining | bytes | canonical asset bytes |

Asset-kind codes are `1` texture, `2` material, `3` tie, `4` shrub, `5` tfrag,
`6` moby, `7` animation, `8` sound, `9` sky, and `10` gameplay. The kind and
canonical-format version are identity inputs; names, tags, provenance, source
paths, and timestamps are not. Canonicalizers own their canonical bytes and must
change their format version when the meaning or encoding changes.

## Entity identity

Entity IDs are non-empty random UUIDv4 values serialized as lowercase canonical
`8-4-4-4-12` strings. Save/load retains the value. Duplicate/copy creates a new
Entity ID while asset references may remain unchanged.

## Project schema gate

Every `forge-project.json` contains an integer `schemaVersion`; version zero is
the only supported value today. Missing, non-integer, and negative versions are
invalid. Lower versions require an explicit ordered migration. A version greater
than the application supports must be rejected before any migration or write, so
unknown future data is never overwritten. The rest of the manifest/content shape
is intentionally left to M1-005.
