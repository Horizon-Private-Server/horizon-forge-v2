# Asset catalog v0

The per-user asset cache is rooted at the application `assets` directory:

```text
assets/
├── catalog-v0.json
├── .catalog-v0.lock
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
deduplicated and sorted ordinally before persistence. Texture appearances may also
record a typed owner kind, owner class ID, role, slot, and whether the source bytes
are restorable; missing-source placeholders are explicitly non-restorable.

Metadata can be merged by Asset ID without supplying or rewriting canonical blob
bytes. Re-indexing preserves the original import timestamp while recording the
latest importer version and additional aliases, tags, or source appearances.

## Commit rules

1. Canonical bytes are written and flushed to a same-directory `.partial` file.
2. The complete blob is renamed to its immutable Asset-ID path.
3. A complete replacement catalog is written and flushed to a `.partial` file.
4. The replacement catalog is atomically renamed over the previous catalog.

Temporary-file writes are buffered; each complete file still receives an explicit
durable flush before its final rename. Syncing every write of an unpublished
temporary file adds no committed-state protection. UYA import checkpoints follow
the same rule and are published only after the level's catalog commit succeeds.

Batch insertion prepares each unique Asset ID once with at most sixteen concurrent
blob writers/verifiers. Workers reference the supplied canonical bytes rather than
copying or queuing additional levels. The writer lease covers the entire batch;
metadata merges retain input order and publication waits for every worker. Failure
or cancellation waits for workers to stop and clean their partials before returning.
This does not parallelize reads from the importer's seekable ISO stream.

Source-appearance matching compares normalized records directly, excluding the
replaceable Moby sound and sound-program enrichment fields. When source, alias or
tag counts or the importer version change, the merged entry is already known to
differ. Potentially unchanged entries still use complete serialized comparison so
sound-enrichment updates and ordered metadata retain their existing behavior.

Cancellation or failure never exposes a partial final blob or a catalog row whose
blob was not committed first. A failure between steps 2 and 4 can leave a complete,
unindexed blob; retry verifies and reuses it. Opening the store removes identifiable
stale partial files.

Current writers coordinate through an exclusive handle on `.catalog-v0.lock` and
reload the committed catalog before merging changes. This applies across store
instances and cooperating Forge processes. Before replacement they check that the
committed revision still matches their input. A stale garbage-collection preview
also fails when metadata changes. Cleanup under this lease covers catalog-root and
blob partials; it does not delete another subsystem's import checkpoints.

Unchanged normalized metadata does not rewrite the catalog. A generated catalog
larger than the existing 64 MiB reader limit is rejected before replacement, keeping
the previous catalog and in-memory snapshot usable. Complete unindexed blobs can
remain after a failed publication, as with other interrupted commits.

## Runtime snapshots and query indexes

Store instances read a consistent committed snapshot. Catalog opens check a bounded
SHA-256 digest of the file's actual contents; unchanged contents reuse validated
entries, semantic revision, and ID/kind/class ordering. Same-size, timestamp-preserving
edits or replacements invalidate reuse. Corrupt or missing catalogs are not hidden
by a cached snapshot. Concurrent cold opens share hydration without coupling their
individual cancellation tokens.

The shared cache holds at most four snapshots, weighted by at most 64 MiB of
serialized catalog content. This is a retention bound, not a 64 MiB managed-heap
limit; decoded strings and indexes take additional memory. Only one cold hydration
runs at a time. Cached entries are treated as immutable, and committed updates swap
the owning store's snapshot as one reference so entries and cursor revision agree.

Content checking still reads and hashes the catalog on each open. Removing those
reads safely and measuring filtered queries, import commit scaling, and resource
caps remain MPERF work; this change does not introduce a new persistent database
format.

Queries are exact-match, sorted by Asset ID, and limited to at most 1,000 results.
Multiple tag filters use all-tags semantics.
