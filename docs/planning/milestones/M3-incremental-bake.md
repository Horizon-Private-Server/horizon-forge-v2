# M3 — Incremental bake

Priority: P0  
Depends on: M2  
Task register: [M3 tasks](../tasks/M3-tasks.md)

Tasks M3-004 through M3-008 begin after the
[M3A SDK archive round-trip](M3A-sdk-archive-round-trip.md) exit gate. This gives
all later bakers a proven container writer instead of accumulating Forge-local
binary assembly paths.

## Outcome

Supported edits bake deterministically into transactional staging output while
unchanged layers are reused and unsupported content remains byte-exact.

## Deliverables

- Layer dependency graph, fingerprints, snapshots, and staging transactions.
- Opaque pass-through model for code overlays/unknown content.
- UYA bakers for the agreed P0 world, instance, gameplay, and lighting content.
- Capability discovery and actionable validation.
- Deterministic clean rebuild and incremental invalidation fixtures.

## Exit gate

Changing one supported entity rebakes only its transitive outputs; cancelling or
failing preserves the last successful staging manifest; a clean rebuild is
byte-equivalent; and every opaque payload hash matches its imported source.
