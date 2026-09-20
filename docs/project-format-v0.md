# Forge project format v0

A Forge project is portable project data plus optional project-owned immutable
assets. Global vanilla assets remain in the per-user catalog and are referenced by
Asset ID.

```text
MyLevel/
├── forge-project.json
├── content/
│   └── project.json
└── assets/
    └── ab/
        └── ab….blob
```

`recovery/` and `staging/` are added by their owning milestones and are not part of
the authoritative v0 project format.

## Manifest

`forge-project.json` contains:

- `schemaVersion`: integer `0`;
- `projectId`: stable lowercase UUIDv4;
- `name`: display name;
- `target`: game, region, revision, and bake profile;
- `baseLevel`: source game, region, revision, numeric level, and ISO fingerprint;
- `baseLevel.missingAssetCount`: source instances omitted because their global
  asset was unavailable at creation time;
- `content`: portable project-relative path to the content document.

The development ISO and clean-ISO paths are machine settings and never appear in
the project.

## Content

`content/project.json` has its own `schemaVersion` and contains ordered `entities`
and project-attached `assets`.

Each entity records a stable Entity ID, name, logical layer, position/quaternion/
scale transform, an optional `{ id, kind }` Asset-ID reference, and optional source
provenance (`game`, `level`, source `section`, and source-instance index). Transform
edits change only the entity; they do not attach or copy an asset.

Each attached asset records its Asset ID, kind, canonical-format version, immediate
parent Asset ID, and byte size. Its canonical bytes are stored at
`assets/<first-two-ID-characters>/<asset-id>.blob`. Paths are derived from validated
IDs rather than persisted in project JSON.

## Copy-on-write

An edit to immutable resource bytes computes a new Asset ID and writes only to the
current project's `assets/` directory. By default, every entity in that project
referencing the edited source is redirected to the derived ID. **Make unique**
redirects only the selected entity. The returned reference-change record is the
undo unit; undo restores prior references and leaves an unreferenced blob eligible
for later explicit cleanup.

Resolution checks project-attached assets before the global catalog. No write path
targets the global catalog.

## Compatibility

Both JSON documents reject missing, malformed, duplicate, or unsupported schema
versions. Unknown JSON members are rejected so opening and saving cannot silently
discard data produced by another build. Serialization is deterministic for an
unchanged in-memory project and emits only forward-slash relative content paths.
