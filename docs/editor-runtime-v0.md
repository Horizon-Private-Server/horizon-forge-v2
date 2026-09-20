# Editor runtime v0

`EditorRuntime` is the authoritative, UI-independent editing session. It owns one
`ForgeProjectWorkspace`; React and Three.js consume snapshots and submit commands
but do not own or mutate project state.

## Session lifecycle

- `OpenEditorProject(path, autosaveSeconds)` flushes a dirty prior session to a
  recovery snapshot, opens the requested project, and returns its snapshot.
- `QueryEditor` returns the current snapshot without mutation.
- `SaveEditorProject` atomically writes the explicit project files and clears dirty
  state.
- `CloseEditorProject` writes a recovery snapshot when dirty, then clears the
  session. Electron closes the host's stdin before termination so this also runs
  during normal application shutdown.
- A mutating command restarts the idle recovery timer. A zero delay disables the
  timer for tests; user settings currently constrain it to 5–3600 seconds.

Recovery never replaces the explicit save. It remains a separately selectable
snapshot under the existing project-format v1 policy.

## Commands

Each command contains a lowercase canonical UUID, a known command kind, and a
unique list of existing Entity IDs. Validation finishes before any mutation or
event is produced.

| Kind | Data | Effect |
| --- | --- | --- |
| `setSelection` | zero or more Entity IDs | Replaces shared selection; not dirty |
| `renameProject` | nonblank name | Renames the manifest and marks dirty |
| `updateTransform` | one Entity ID and finite transform | Updates one entity and marks dirty |
| `renameEntity` | one Entity ID and nonblank name | Renames one entity and marks it dirty |
| `setEntityLayer` | one or more Entity IDs and nonblank layer | Reparents entities to a layer |
| `setEntityState` | one or more Entity IDs and a partial state | Changes hidden, disabled, or locked state |

Commands and their fields use the binary bridge codec. JSON serialization in the
domain contract test proves that command values are data, not executable UI
callbacks; JSON is not used on the Electron/.NET wire.

## Queries and events

The snapshot includes project identity and target, complete entities, shared
selection, dirty/migration state, last event sequence, capabilities, tools, and
bounded background diagnostics. Entity snapshots include persisted state plus
derived dirty, invalid, and missing-asset flags. Three.js objects are projections
keyed by Entity ID and are never returned as authoritative state.

Events have a monotonically increasing sequence, Unix-millisecond timestamp,
kind, optional originating command UUID, affected Entity IDs, and optional
message. The runtime retains the newest 1,024 events and serves at most 1,024 per
query. Diagnostics retain the newest 100 entries.

## Extension boundary

Capabilities and tool descriptors expose concrete supported behavior without a
service locator. P0 implements no plugin loading or collaboration transport.
Future peers can replay the same validated serializable commands and ordered
events; conflict authority and undo/redo remain later tasks.
