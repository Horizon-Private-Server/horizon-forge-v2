# UYA content-import planning v1

Status: Planning and atomic batches implemented; UYA/RC1/GC/DL donor support reconciled 2026-11-06

The planning operation inventories one donor level from a verified clean UYA, RC1,
GC, or DL source and compares it with an existing UYA target project. It is deliberately
read-only: the source ISO is opened with read access, the global asset catalog uses its
non-mutating open path, and the target workspace is never saved.

## Request and verification

Bridge opcode `PlanContentImport` accepts the target project path, catalog path,
verified source ISO path and MD5, donor game/level, and zero or more explicit `add` or
`replace` family selections. Converted donors allow replacement only. Before family
enumeration, the host verifies:

- the target is a UYA NTSC-U revision 1.00 project;
- the disc profile is supported: UYA/RC1/DL NTSC-U 1.00 or GC NTSC-U 1.01;
- the requested MD5 matches the selected donor disc; UYA donors must also match the
  target project's source fingerprint; and
- the requested donor level is present and structurally readable.

The plan records both the verified disc MD5 and the extracted donor level-WAD SHA-256.
UYA parsing reuses the base-project reader. Converted donors use the SDK's
`LevelContentInventoryReader` and `LevelContentConverter`, followed by UYA project
materialization and a bounded transient catalog. Conversion failures disable the
affected family before apply. Progress covers hashing, inventory, and plan construction;
bridge cancellation is checked during hashing, tfrag extraction, family planning, and
before returning the result.

For UYA donors, Forge keeps one verified disc identity and one extracted donor snapshot in memory. Both
are keyed by file versions, with the extracted snapshot additionally keyed by catalog
version and donor level. Changing selections and applying the reviewed plan reuse the
snapshot; changing levels reuses disc verification while extracting the new level. Plan
construction still runs against the live editor projection. A changed ISO or catalog
invalidates the corresponding cache, and a change during verification or extraction
fails without caching the mixed result.

Converted-donor caching retains verified identities per source game and one bounded
extracted/conversion snapshot keyed by disc version, source/target game, and donor level.
Model/sound preparation reads the current catalog; plans bind its revision so changes
cannot authorize a stale apply. Neither path publishes converted data to the global catalog.

## Deterministic plan

Plan schema version 3 returns:

- a stable batch ID derived from the schema, target project/fingerprint, donor
  game/region/revision, hashes and level, source catalog revision and inventory
  identities, and sorted selections;
- the capability schema and target/source identity;
- every contract family in stable matrix order with its source state, selected mode,
  selectability, default state, and estimated source bytes;
- namespaced stable add, reuse, remap, remove, and protect sets;
- dependency reasons, class conflicts, capacity diagnostics, warnings, and blockers;
  and
- aggregate byte estimates, warnings, and blockers for selected families; and
- typed external-reference requirements with nullability and the exact compatible,
  retained target Entity IDs available to the apply UI.

Donor entity keys use family, native section, and source index rather than temporary
project UUIDs. Reuse entries use canonical Asset IDs. Replace planning protects locked
entities, targets referenced by content outside the replacement set, and targets held by
project-level settings. Collision linked to a replaced TIE or shrub is owned content rather
than an inbound protection edge: exclusively attached pieces are removed with the instance,
while shared pieces retain only their surviving instance links. Cyclic reference graphs require no recursive traversal. Class
conflicts identify the exact donor class slots requiring a later deterministic custom
allocation; M16-004 supplies those final target slots during static-family commit.
Apply sends one explicit Map or Clear choice for every external Moby PVar requirement.
Clear is accepted only for nullable fields; Map is revalidated against the live retained
entity kind, and missing, duplicate, stale, incompatible, or unused choices fail before
the atomic batch commit.

All family implementation gates remain authoritative. A caller may request a blocked
family to inspect its complete dry run, but the family and aggregate plan retain the
M16-001 blocker. No plan is a commit authorization.

## Bounds and failure behavior

The planner accepts at most 18 selections, inventories at most 100,000 source items,
bounds each warning/blocker collection to 64 entries, validates bridge list counts, and
uses the existing 64 MiB frame limit. Native/project capacity overflows become explicit
family blockers. Unknown/duplicate selections and invalid modes fail at the boundary.

Failures and cancellation leave the project, ISO, catalog metadata, and catalog blobs
unchanged. A missing catalog may be represented as an empty read-only view without
creating its directory.

## Verification

Synthetic level-40-to-level-41 coverage verifies stable batch IDs, exact candidate and
reuse sets, class-conflict/remap reporting, referenced-survivor protection, dependency
reasons, byte estimates, a cyclic PVar graph, map-group capacity overflow, early
incompatible-source rejection, cancellation, read-only catalog opening, and deterministic
C#/TypeScript bridge serialization. Retail donor bytes remain a local qualification step.
