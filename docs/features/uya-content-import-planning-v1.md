# UYA content-import planning v1

Status: M16-002 implemented (2026-10-18); atomic import batches start in M16-003

The planning operation inventories one donor level from the verified clean UYA NTSC-U
1.00 source and compares it with an existing target project. It is deliberately
read-only: the source ISO is opened with read access, the global asset catalog uses its
non-mutating open path, and the target workspace is never saved.

## Request and verification

Bridge opcode `PlanContentImport` accepts the target project path, catalog path,
verified source ISO path and MD5, donor level, and zero or more explicit `add` or
`replace` family selections. Before family enumeration, the host verifies:

- the target is a UYA NTSC-U revision 1.00 project;
- the disc profile is the supported clean UYA image;
- the requested MD5 matches the disc and the target project's source fingerprint; and
- the requested donor level is present and structurally readable.

The plan records both the verified disc MD5 and the extracted donor level-WAD SHA-256.
Parsing reuses the base-project reader, so project creation and donor planning interpret
the same native structures. Progress covers hashing, inventory, and plan construction;
bridge cancellation is checked during hashing, tfrag extraction, family planning, and
before returning the result.

Forge keeps one verified disc identity and one extracted donor snapshot in memory. Both
are keyed by file versions, with the extracted snapshot additionally keyed by catalog
version and donor level. Changing selections and applying the reviewed plan reuse the
snapshot; changing levels reuses disc verification while extracting the new level. Plan
construction still runs against the live editor projection. A changed ISO or catalog
invalidates the corresponding cache, and a change during verification or extraction
fails without caching the mixed result.

## Deterministic plan

Plan schema version 2 returns:

- a stable batch ID derived from the schema, target project/fingerprint, donor hashes
  and level, and sorted selections;
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
