# Asset catalog v0

The per-user asset cache is rooted at the application `assets` directory:

```text
assets/
├── catalog-v0.json
└── blobs/
    └── ab/
        └── abcdef….blob
```

Blob filenames are lowercase Asset IDs. The two-character prefix bounds directory
size. A blob contains canonical asset bytes only; its kind and canonical-format
version are catalog metadata and inputs to the Asset ID defined in
`identity-and-schema-v0.md`.

`catalog-v0.json` records schema version `0` and a list sorted by Asset ID. Each
entry records kind, canonical-format version, byte size, aliases, tags, import
time, importer version, and source appearances. A source appearance records game,
region, revision, level, archive, source index, and source fingerprint. Lists are
deduplicated and sorted ordinally before persistence.

Metadata can be merged by Asset ID without supplying or rewriting canonical blob
bytes. Re-indexing preserves the original import timestamp while recording the
latest importer version and additional aliases, tags, or source appearances.

## Commit rules

1. Canonical bytes are written and flushed to a same-directory `.partial` file.
2. The complete blob is renamed to its immutable Asset-ID path.
3. A complete replacement catalog is written and flushed to a `.partial` file.
4. The replacement catalog is atomically renamed over the previous catalog.

Cancellation or failure never exposes a partial final blob or a catalog row whose
blob was not committed first. A failure between steps 2 and 4 can leave a complete,
unindexed blob; retry verifies and reuses it. Opening the store removes identifiable
stale partial files.

Queries are exact-match, sorted by Asset ID, and limited to at most 1,000 results.
Multiple tag filters use all-tags semantics.
