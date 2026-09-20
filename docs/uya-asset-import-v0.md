# UYA global asset canonical format v0

The importer reads every populated entry in the NTSC-U UYA level-info table
through the pinned Ratchet SDK. Each level contributes its vanilla moby, tie, and
shrub model definitions to the per-user asset catalog.

## Canonical asset bundle

Canonical format version `0` stores one model and its ordered textures:

1. ASCII `HFUYA` followed by a zero byte.
2. Signed little-endian 32-bit model length and the original model bytes.
3. Signed little-endian 32-bit texture count.
4. For each texture: a one-byte role (`0` model texture, `1` shrub billboard),
   signed little-endian 32-bit PIF length, and normalized PIF bytes.

The model class ID is an alias and is not part of identity. Game, revision, level,
source archive/index, ISO fingerprint, and importer version are catalog metadata.
Changing this binary layout or its interpretation requires a new canonical format
version.

## Resume and commit behavior

The importer validates the clean ISO fingerprint, commits each level as one catalog
batch, then atomically checkpoints that level beneath
`imports/uya/<iso-fingerprint>/<importer-version-hash>.json`. A cancellation or
crash can therefore repeat at most the active level; catalog deduplication makes
that retry safe. A completed checkpoint can be reused without the source ISO being
present, and imported blobs remain independent of the source path.

The importer version is `forge-uya-v1+<pinned-sdk-revision>`. A different source
fingerprint or importer version receives an independent checkpoint and is scanned
incrementally into the same content-addressed catalog.

Setup also exposes an explicit re-import action. It ignores a completed checkpoint,
rescans every populated level, and merges the results into the same catalog; blob
identity still prevents duplicate storage. Cancellation leaves the previously
imported catalog usable and records the new scan's resumable checkpoint.

If an individual texture cannot be decoded, the importer preserves its slot with a
deterministic magenta-and-black checkerboard PIF and tags the asset
`texture:placeholder`. A model is counted as failed only when its model data cannot
be read; it does not abort or falsely complete the rest of its level.
