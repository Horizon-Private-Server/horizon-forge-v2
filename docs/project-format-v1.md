# Forge project format v1

Version one preserves the portable manifest, content, and project-asset layout
from [version zero](project-format-v0.md). It adds an explicit `documentType` to
prevent a manifest and content document from being mistaken for each other:

- `forge-project.json`: `"documentType": "forge-project"`;
- the content document: `"documentType": "forge-project-content"`.

Both documents use `schemaVersion: 1`. Unknown members, a wrong document type,
mismatched manifest/content versions, and versions newer than the running Forge
build are rejected before any project file is written.

## Version-zero migration

Version-zero documents are read into the version-one model in memory. Opening or
inspecting a v0 project does not change it on disk. The project hub identifies the
pending migration and requires the user to choose **Upgrade and open**. That
explicit action saves the v1 documents through the ordinary journaled save path.

## Save transaction

Before replacing an existing explicit save, Forge copies its manifest and content
document into `recovery/.save-journal/`. Each new document is flushed to a unique
temporary file and atomically renamed into place. The journal is removed only
after both replacements succeed. If a save fails or the process exits between the
two replacements, the next open restores both prior documents from the journal
before parsing the project.

## Autosave recovery

Autosave never writes `forge-project.json` or the authoritative content document.
Dirty in-memory state is serialized beneath:

```text
recovery/<unix-milliseconds>-<uuid>/
├── forge-project.json
└── content/project.json
```

Snapshots have a SHA-256 state fingerprint, creation time, entity count, and byte
size available for preview. Identical consecutive states are not duplicated.
History retains at most 10 snapshots and 256 MiB. Snapshots newer than the last
explicit save are offered by the project hub; applying one requires an explicit
user choice and then uses the normal safe-save transaction.
