# Horizon Forge planning index

This directory turns the [Forge v2 specification](../forge-v2-spec.md) and the
[P2 advanced-authoring addendum](P2-next-phase.md) into trackable milestones and
issue-ready tasks. The specifications define behavior; these documents define
delivery order and completion evidence.

## Priority model

- **P0/MVP:** the first usable vanilla NTSC-U UYA editor release.
- **P1:** custom GLB assets and custom-texture quantization.
- **P2:** asset-override, texture-bank, semantic, terrain, and update authoring.
- **Future:** cross-game expansion, collaboration, plugins, macOS, and other work
  explicitly deferred by the specification.

## Delivery order

```text
M0 Foundations
  └─ M1 Projects/assets
       └─ M2 Editing
            └─ M3 Bake foundations (M3-001 through M3-003)
                 └─ M3A SDK archive round-trip
                      └─ M3 Bake writers/qualification (M3-004 through M3-008)
                           └─ M5 Texture inventory/writers (M5-001 through M5-003)
                                └─ M4 Build/patch loop
                                     └─ M5 Diagnostics/qualification (M5-004 through M5-005)
                                          └─ M6 Distribution/P0 release

P1 Custom assets starts only after the relevant M3-M5 contracts are stable.

P2 branches after the P0/P1 contracts it consumes; see its dependency graph rather
than treating M7-M13 as one strictly serial chain.
```

M6 workflow scaffolding may begin earlier, but its release gate remains last.

## Milestones and task registers

| Priority | Milestone | Plan | Tasks |
| --- | --- | --- | --- |
| P0 | M0 — Foundations and walking skeleton | [Milestone](milestones/M0-foundations.md) | [Tasks](tasks/M0-tasks.md) |
| P0 | M1 — Project and asset foundation | [Milestone](milestones/M1-project-foundation.md) | [Tasks](tasks/M1-tasks.md) |
| P0 | M2 — Editor core | [Milestone](milestones/M2-editor-core.md) | [Tasks](tasks/M2-tasks.md) |
| P0 | M3 — Incremental bake | [Milestone](milestones/M3-incremental-bake.md) | [Tasks](tasks/M3-tasks.md) |
| P0 | M3A — SDK archive round-trip | [Milestone](milestones/M3A-sdk-archive-round-trip.md) | [Tasks](tasks/M3A-tasks.md) |
| P0 | M4 — Build and patch loop | [Milestone](milestones/M4-build-patch.md) | [Tasks](tasks/M4-tasks.md) |
| P0 | M5 — Vanilla texture optimization | [Milestone](milestones/M5-texture-optimization.md) | [Tasks](tasks/M5-tasks.md) |
| P0 | M6 — Distribution and release | [Milestone](milestones/M6-distribution.md) | [Tasks](tasks/M6-tasks.md) |
| P1 | P1 — Custom asset pipeline | [Milestone](milestones/P1-custom-assets.md) | [Tasks](tasks/P1-tasks.md) |
| P2 | M7-M13 — Advanced authoring | [Plan and requirements](P2-next-phase.md) | [Tasks](tasks/P2-tasks.md) |

See [P0/MVP scope](P0-MVP.md) for the release boundary.

## Feature specifications

Feature-specific design, acceptance criteria, and tasks live together when keeping
them in a milestone-wide task register would split one implementation plan:

- [Asset Explorer v0](../features/asset-explorer-v0.md) — M2-021 through M2-026
- [Skybox Editor v0](../features/skybox-editor-v0.md) — M2-027 through M2-030
- [Collision Editor v0](../features/collision-editor-v0.md) — M2-031 through M2-035
- [Instanced TIE Collision v0](../features/instanced-tie-collision-v0.md) — M2-036 through M2-040
- [Collision Type Painting v0](../features/collision-type-painting-v0.md) — M2-041 through M2-044
- [HUD bank and sprite-ID contract v0](../features/hud-bank-contract-v0.md) — M8-001 through M8-005
- [FX texture inventory and label contract v0](../features/fx-texture-contract-v0.md) — M8-006 through M8-008

## Task conventions

Task IDs are stable and use `<milestone>-<sequence>`, for example `M2-006`.
Milestone registers link to any tasks owned by a feature specification. Each task
contains:

- requirement IDs from the specification;
- dependencies on other task IDs;
- the smallest useful deliverable;
- acceptance criteria describing observable behavior; and
- verification that must remain runnable or be recorded manually.

Recommended task states are `not-started`, `in-progress`, `blocked`, `review`, and
`done`. In-progress state belongs in the issue tracker once tasks are imported;
this baseline marks only acceptance-complete tasks with `Status: ✅ Complete`.
Unmarked tasks are not complete.

## Definition of ready

A task is ready when:

1. every dependency is done or the task documents a safe mock/fixture boundary;
2. its required input format or upstream API exists;
3. no unresolved product choice changes its acceptance criteria; and
4. test data can be used without committing proprietary game content.

## Definition of done

A task is done when:

1. every acceptance criterion passes;
2. its automated checks run in the appropriate local/CI command;
3. required manual evidence is linked or recorded;
4. errors and cancellation preserve the last known-good state;
5. relevant specification and planning links remain accurate; and
6. no unrelated deferred abstraction was added.

Milestone completion additionally requires its packaged-app exit gate.

## Game-data policy

No proprietary ISO, WAD, model, texture, or extracted game payload is committed to
the repository or uploaded as a public CI artifact. Unit and CI tests use synthetic
fixtures, structural test builders, hashes, or redistributable authored data.
Full-ISO integration tests are local/manual unless a separately authorized private
environment is established later.
