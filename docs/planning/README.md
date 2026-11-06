# Horizon Forge planning index

This directory turns the [Forge v2 specification](../forge-v2-spec.md), the
[P2 advanced-authoring addendum](P2-next-phase.md), and the
[P3 content-expansion addendum](P3-content-expansion.md) into trackable milestones
and issue-ready tasks. The specifications define behavior; these documents define
delivery order and completion evidence.

The [intermediate performance milestone](milestones/MPERF-intermediate-performance.md)
adds measurable requirements and tasks for optimizing the existing feature set
before further expansion.

## Priority model

- **P0/MVP:** the first usable vanilla NTSC-U UYA editor release.
- **P1:** custom GLB assets and custom-texture quantization.
- **P2:** asset-override, texture-bank, semantic, terrain, and update authoring.
- **P3:** custom TIE/tfrag assets, cross-map imports, sound banks, moby simulations, and
  generated occlusion.
- **Intermediate stabilization:** MPERF improves existing workflows before further
  feature expansion; it does not renumber P0-P3 or waive their release gates.
- **Delivered donor expansion:** RC1, GC, and DL asset placement and map-content
  replacement into UYA; M16 owns donor-import qualification.
- **Future:** additional writable game targets, gameplay translation, collaboration,
  plugins, macOS, and other work explicitly deferred by the specification.

## Current backlog inventory

Updated 2026-11-06 after source review and maintainer sign-off. Of 210 task IDs,
168 are marked complete and 42 remain unclosed. Historical P1 scope and explicit
future work are included in the unclosed count. Automated inventory checks passed;
completion of the tasks below is recorded from the maintainer's sign-off.

### Maintainer sign-off

The maintainer marked these 44 task IDs complete on 2026-11-06. No new test-run
metadata, measured budgets, hashes, or capture references accompanied the sign-off;
earlier qualification checklists are retained as historical evidence. Existing task
IDs, acceptance criteria, and supported feature limits remain in place.

| Area | Tasks closed |
| --- | --- |
| CI and editor features | M0-009; M2-021–026; M2-033–040; M2-044 |
| Build and textures | M4-006; M5-005 |
| HUD/FX and scene semantics | M8-005; M8-008; M11-005 |
| Custom TIEs, sounds and imports | M14-007; M15-006; M16-009, including delivered RC1/GC/DL donor replacement |
| Ocean simulation | M17-003; M17-005 |
| Authored occlusion coverage | M18A-001; M18A-003–008 |
| Performance | MPERF-001–011 |

M18-009 also closes under its existing rule: it tracks M18A's exit gate rather than
another implementation. M0, M4, M5, M8, M11, M14, M15, M16, M17, M18A, and MPERF
now have all their tasks complete. M3, M3A, M10, and the skybox tasks were already
complete. Broader M18 and combined release gates remain open.

### Implementation and acceptance gaps

| Owner | Remaining work |
| --- | --- |
| [M2-014–020](tasks/M2-tasks.md#remaining-uya-data-baseline) | Reconcile all 39 core-slot byte owners and retain all-level import/migration evidence. Geometry composition, native groups, Moby PVars, ship references, and occlusion mappings are implemented; general camera/sound shared-PVar handling and settings beyond the verified prefix remain limited. |
| [M6-006](tasks/M6-tasks.md#m6-006--complete-accessibility-diagnostics-and-support-documentation) | Redacted support-bundle preview/export, support/recovery documentation, and accessibility completion. Structured logging already exists. |
| [M18-001–008/010](tasks/P3-tasks.md#m18--occlusion-generation-and-analysis) | Native contract/baselines, automatic camera-route coverage, conservative reference guarantees, production qualification, integrated diff/POV tooling, visibility-cost graph/heatmap, broader native-group coverage, and streamed-terrain rebuilding remain. Manual coverage authoring is complete through M18A/M18-009. |

### Qualification and release work

| Owner | Remaining gate |
| --- | --- |
| [M6-001–005/007](tasks/M6-tasks.md) | Clean-machine packaging/publishing matrices, release rehearsals, performance captures, and P0 sign-off remain unclosed. MPERF completion does not automatically close these separate release tasks. |
| [M7-001–003/005/007](tasks/P2-tasks.md#m7--authoring-foundations-and-workspace-chrome) | Packaged authoring/accessibility qualification. |
| [M9-009](tasks/P2-tasks.md#m9-009--integrate-mobypvar-reference-enrichment-and-qualify-m9) | Real-level Moby/PVar, packaged Linux/Windows, and PCSX2 evidence. |
| [M12-008](tasks/P2-tasks.md#m12-008--qualify-editable-tfrags-end-to-end) | Editable/custom-tfrag qualification, including streamed pieces, portability, recovery, and PCSX2. Custom-TIE sign-off does not close custom terrain. |
| [M13-001/006/007](tasks/P2-tasks.md#m13--full-package-updater-and-p2-qualification) | Windows updater activation, rollback/interruption, unsupported layouts, and the combined P2 release gate. |
| [M19-001–003](tasks/P3-tasks.md#m19--p3-integration-and-release-qualification) | Combined portable qualification project, regression matrix, and P3 release sign-off remain unstarted. |

### Explicitly deferred scope

- [M1-009–011](tasks/M1-tasks.md#m1-009--publish-global-vanilla-asset-lookup-manifests):
  checked-in global lookup manifests, lookup-based recovery, and optional common names.
- [P1-002/005/006](tasks/P1-tasks.md): historical broad model requirements remain
  tracked separately. Custom TIEs are delivered and qualified through M14; custom-tfrag
  qualification remains M12-008. Custom shrub/Moby authoring, skins, and animation are deferred.
- [P3 deferred work](P3-content-expansion.md#deferred-work): additional writable games,
  gameplay translation, DL mission-instance merging, general audio authoring, and other
  explicitly excluded features. RC1/GC/DL donor replacement is complete under M16.

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

P3 branches after the specific P1/P2 contracts each lane consumes; M19 release
qualification remains after M13-007 and the M14-M18 exit gates.
```

M6 workflow scaffolding may begin earlier, but its release gate remains last.

MPERF begins from the currently implemented workflows and freezes baselines before
optimization. It can run ahead of unfinished P3 features, including M18; see its
[task dependency graph](tasks/MPERF-tasks.md#delivery-order).

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
| P3 | M14-M19 — Content expansion and runtime fidelity | [Plan and requirements](P3-content-expansion.md) | [Tasks](tasks/P3-tasks.md) |
| P3 | M18A — Occlusion coverage authoring | [Milestone and requirements](milestones/M18A-occlusion-coverage.md) | [Tasks](tasks/M18A-tasks.md) |
| Intermediate | MPERF — Intermediate performance improvements | [Milestone and requirements](milestones/MPERF-intermediate-performance.md) | [Tasks](tasks/MPERF-tasks.md) |

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
- [UYA moby instance contract v0](../features/uya-moby-instance-contract-v0.md) — M9-001 through M9-003
- [MobyDex entry schema v1](../features/mobydex-v1.md) — M9-004 onward
- [Updater package and install contract v1](../features/updater-package-contract-v1.md) — M13-001 onward
- [UYA custom TIE class contract v1](../features/uya-custom-tie-class-contract-v1.md) — M14-001 onward
- [UYA 989snd bank and remap contract v1](../features/uya-989snd-contract-v1.md) — M15-001 onward
- [UYA content-import capability contract v1](../features/uya-content-import-contract-v1.md) — M16-001 onward
- [UYA content-import planning v1](../features/uya-content-import-planning-v1.md) — M16-002 onward
- [Content-import batches v1](../features/content-import-batches-v1.md) — M16-003 onward
- [Moby simulation lifecycle v1](../features/moby-simulation-lifecycle-v1.md) — M17-001 onward
- [UYA ocean simulation v1](../features/uya-ocean-simulation-v1.md) — M17-003 onward
- [Occlusion coverage project contract v1](../features/occlusion-coverage-project-v1.md) — M18A-002

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

When reconciling a task, identify remaining work as **implementation**,
**qualification**, or **deferred scope**. Link later owning tasks for historical or
scoped deliverables. Reserve `✅ Complete` for the stated acceptance scope and retain
manual gates even when all automated checks pass.

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
