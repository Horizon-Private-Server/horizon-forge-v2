# M1 — Project and asset foundation

Priority: P0  
Depends on: M0  
Task register: [M1 tasks](../tasks/M1-tasks.md)

## Outcome

A user can complete UYA setup, import the shared vanilla asset catalog once,
create a portable project from a base level, save it, move it, reopen it, and
recover interrupted work without copying common assets into the project.

## Deliverables

- Flat settings and application-data paths.
- Clean ISO validation and transactional development ISO copy.
- Content-addressed immutable blob store and tagged catalog.
- Incremental all-map UYA tie/shrub/moby import.
- Versioned project/entity/provenance format.
- Project hub, base-level creation, resolution, save, migration, and recovery.
- Missing-asset repair and safe catalog maintenance.

## Exit gate

A base project survives close, filesystem move, and reopen on a clean app session;
global assets remain deduplicated; project edits cannot mutate the global cache;
and no project file contains a machine-specific source/development ISO path.

