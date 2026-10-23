# Content-import batches v1

M16-003 adds the game-neutral project transaction used by later cross-map family
importers. It records what a verified plan committed without retaining the donor ISO
path or any other machine-local path.

## Persisted provenance

`ForgeProjectContent.importBatches` is an append-only ledger. Each v1 record contains:

- the stable batch ID, batch schema, plan schema, and capability schema versions;
- the import timestamp and donor game, region, revision, level, disc fingerprint, and
  verified level hash;
- the selected family and add/replace mode pairs; and
- sorted stable-ID sets for added, reused, replaced, and protected content.

Batch IDs, family names, and stable IDs are bounded, unique, portable values. Stable
IDs cannot contain rooted paths, separators, or control characters. The record has no
donor file-path field.

## Atomic command

`ContentImportBatchCommit` carries the expected target fingerprint, a fully composed
next project-content state, the batch record, and the canonical attached-asset bytes.
The workspace rejects stale plans and requires callers to retain the existing asset
metadata and batch ledger; only the transaction may append those records.

The commit has four checkpoints:

1. Compute every Asset ID and validate kind, canonical version metadata, size, parent,
   duplicate payload, and existing-metadata consistency.
2. Write or integrity-check content-addressed blobs with safe partial-file replacement.
3. Validate the complete prospective project, including entities, references, custom
   allocations, attached metadata, and batch provenance.
4. Publish the complete `ForgeProjectContent` object with one state assignment.

Cancellation or an exception before publication leaves the prior project state in
place and removes blobs created by that attempt. A process interruption can leave an
unreferenced content-addressed blob, but cannot expose a partial batch; such blobs use
the existing project-asset retention and maintenance policy.

## Dependency-safe removal

The Imports panel can ask the host for an authoritative removal preview for a batch.
The host recomputes dependencies from the live project and separates batch-owned
content into deletions and explained survivors. Add-mode entities, groups, Moby sound
graphs, class allocations, and attached-asset metadata are removed only when retained
content no longer references them. Shared and protected records are reported separately.

Replace-mode results are retained. The v1 ledger records provenance and stable IDs, but
does not store a snapshot of the displaced target content, so it cannot safely restore
the previous skybox, settings, lights, or family records. The preview states this rather
than presenting a destructive partial reversal.

Apply requires the preview's target fingerprint and recomputes the plan before one
atomic editor command. A changed project rejects the stale preview. Removal participates
in undo/redo and keeps the append-only batch record. Content-addressed blobs are retained
for undo and recovery; ordinary asset maintenance may collect them after they become
unreferenced.

## History, recovery, and compatibility

The editor runtime wraps publication in one history entry. Undo restores the complete
pre-import snapshot, redo restores the complete imported snapshot, and blobs remain
available under normal deduplicated retention. Recovery snapshots and ordinary saves
already serialize the whole content state, so the batch ledger, assets, references,
allocations, and entities recover together. ZIP project transfer carries the same
project-relative blobs and provenance.

Project schema 21 introduces the ledger. Older supported projects migrate to an empty
ledger in memory and are not rewritten until save. As elsewhere in the project format,
a newer project schema, a newer batch schema, or an unknown field is rejected rather
than silently discarded.
