# ADR-0001: Reuse map-o-matic selectively

- Status: Accepted for migration; renderer backend selection is deferred to M0-007
- Date: 2026-09-19
- Task: M0-006
- Requirements: FR-SCENE-001, FR-SCENE-002, NFR-MAINT-001,
  NFR-PERF-003, NFR-PERF-005

## Context

Forge needs a runnable UYA viewport without inheriting map-o-matic's browser-only
loading architecture or coupling the editor to a renderer backend before the
WebGL/WebGPU benchmark. This audit traces the current viewer, identifies reusable
parts, and records what must change before code enters Forge.

## Audited source and provenance

The audited source is `badger41/ratchet-map-o-matic` at commit
`ed6142838156dcd3ac4f2bb8f2c8d98107647eee` (local sibling checkout
`../ratchet-map-o-matic`). Git history at that revision records one contributor,
`badger41 <gamester440@gmail.com>`. The source repository has no `LICENSE` file.
Reuse was requested by the owner as part of this Forge migration; preserve this
ADR and the source commit in migrated-file history. Do not infer a general
third-party license from the target repository's MIT license.

Runtime dependencies relevant to proposed reuse are:

| Dependency | Audited version | License | Forge status |
| --- | --- | --- | --- |
| Three.js | 0.184.0 | MIT | Already pinned at the same version |
| React | 19.2.7 | MIT | Already pinned; renderer logic must not depend on React |
| Mantine | 9.3.2 | MIT | Already pinned; old viewer UI is not reused |
| fflate | 0.8.3 | MIT | Not needed; custom ZIP handling stays out of M0 |
| lucide-react | 1.21.0 | ISC | Not needed; do not add it for viewport migration |

The baseline source passes all 26 tests and its production build. The build emits
these useful size signals before compression: `three.tsl` 1.33 MB, the viewer
screen 194 KB, the model worker 227 KB, and four generated SDK workers at
2.66-2.71 MB each. The SDK workers are intentionally excluded from Forge.

## Existing data flow

```text
MapLoader React state
  -> cache lookup by source URL
  -> fetch entire WAD/custom ZIP
  -> clone the source once into each of four generated TypeScript SDK workers
  -> build common/terrain/moby/tie package parts
  -> merge parts and separately parse gameplay
  -> store packed bytes plus duplicated gameplay metadata in IndexedDB
  -> reload the packed bytes through an `idb:` pseudo-source
  -> create Blob URLs for glTF, buffers, and images
  -> load manifests and normalized package metadata
  -> MapSceneRenderer concurrently loads terrain, sky, ties, shrubs, and mobys
  -> Three.js scene, GPU resources, and animation loop
```

Loose HTTP packages enter at the manifest-loading step. On a successful load,
`MapSceneRenderer` effectively owns the `LoadedMapPackage`; disposing the current
scene revokes package object URLs. React guards late UI updates, but it does not
cancel the underlying work.

Forge replaces the first half of this flow:

```text
Pinned .NET host/SDK -> app-owned render package or temporary package directory
  -> narrow preload/package URL boundary
  -> renderer package reader
  -> selected Three.js backend
```

The clean ISO, WAD parsing, gameplay parsing, conversion, and persistent cache
remain host responsibilities. They must not be reimplemented in TypeScript.

## Decision

Do not copy the viewer as a subsystem. Reuse small proven algorithms and adapt the
package projection and UYA scene families behind Forge-owned lifecycle boundaries.
M0-007 will exercise the same TSL scene graph with Three.js
`WebGPURenderer({ forceWebGL: true })` and the normal WebGPU backend. Only its
winner becomes Forge production code.

### Port with only import/name changes

| Source module | Why it is reusable | Dependencies and required tests |
| --- | --- | --- |
| `services/mapPackages/binaryBuffer.ts` | Correct byte-offset-aware `DataView` construction. | Standard typed-array APIs; retain parser offset tests. |
| `renderer/InstanceData.ts` | Generic model-entry indexing and class grouping. | Package entry types only; add duplicate-class behavior to the package contract test. |
| `renderer/camera/SceneCameraFraming.ts` | Small deterministic initial framing math. | Three.js math types; retain finite/fallback fixture checks. |
| `renderer/ties/TieBloomRange.ts` | Pure spatial range test if tie glow remains enabled. | Three.js vectors; retain `tieBloomRange.test.ts`. |
| `renderer/skybox/SkyboxBackground.ts`, `SkyboxGeometry.ts`, `skyboxMetadata.ts`, `SkyboxStats.ts` | Small UYA-compatible math/metadata helpers. | Three.js and package types; retain existing skybox tests. |

These modules are backend-neutral despite currently importing `three/webgpu` for
math types. Change those imports to the selected common Three.js entry point.

### Adapt before reuse

| Source modules | Useful behavior | Dependencies | Required adaptation |
| --- | --- | --- | --- |
| `services/mapAssets/mapAssetPackage.ts`; `services/mapPackages/loadMapPackage.ts`, `mapPackageTypes.ts`, `mobyPackageEntries.ts` | Package-relative lookup, manifest discovery, optional entries, and object-URL ownership. | Browser URL/fetch/Blob APIs, `valueParsing`, binary helpers, RC1 helpers. | Define one versioned UYA Forge render-package schema; use an Electron `forge-package:` source backed by an app-owned path; validate manifests and entry bounds/duplicates; remove HTTP, IndexedDB, RC1/GC/DL branches. |
| `renderer/MapSceneRenderer.ts`; `RendererCompatibility.ts`, `RendererDisposal.ts`, `RendererTiming.ts`; `FpsCameraController.ts` | Renderer lifecycle, frame loop, resize handling, camera navigation, progress and statistics. | DOM, Three.js WebGPU/TSL, every scene-family controller, React-shaped callbacks. | Split lifecycle from family loaders; inject backend and events; route input through Forge keybinding/tool contexts; make loads cancellable; use one resource tracker; keep each class below the project size target. |
| `renderer/TfragMaterialController.ts`, `TfragAtlas.ts`, `TfragMaterialState.ts`, `ModelFog.ts`, `WaterSurfacePass.ts`, `model-materials/ModelMaterialNodes.ts` | UYA tfrag batching, lighting/color interpretation, fog, texture atlas, and water passes. | Three.js nodes/TSL, canvas, package types, RC1 lighting. | Keep only measured UYA paths; separate CPU geometry preparation from backend material creation; cap atlas dimensions; avoid unconditional non-indexing; remove global debug state and RC1 branches. |
| `renderer/skybox/SkyboxController.ts`, `SkyboxAnimation.ts`, `SkyboxMaterials.ts`, `SkyboxNightStars.ts`, `skyboxDisposal.ts` | Shell visibility/order, UYA blending, animation, reflection source, and stars. | Three.js nodes/TSL, GLTFLoader, package types. | Give textures/materials explicit owners; share the scene cancellation token; remove debug/viewer-window assumptions. |
| `renderer/ties/TieInstanceController.ts`, `TieAmbient.ts`, `TieClassSource.ts`, `TieData.ts`, `TieLighting.ts`, `TieMaterials.ts`, `TiePrimitiveMerge.ts`, `TieTypes.ts`, `tieUtils.ts`, `tieDisposal.ts` | UYA transforms, spatial chunking, instancing, ambient colors, lighting, alpha ordering, and class glTF projection. | Three.js nodes/TSL, package reader, model worker, tfrag water pass. | Consume host-normalized instance data instead of parsing game records; split the 1,294-line controller; make backend-specific instancing a small adapter; consolidate disposal and preserve Entity-ID-to-instance mapping. |
| `renderer/shrubs/ShrubInstanceController.ts`, `ShrubClassSource.ts`, `ShrubData.ts`, `ShrubLightBasis.ts`, `ShrubLighting.ts`, `ShrubMaterials.ts`, `ShrubTypes.ts`, `shrubDisposal.ts` | UYA transform, billboard, lighting, chunking, and instancing behavior. | Same stack as ties. | Apply the tie adaptations; remove duplicated class-source/geometry/disposal helpers; split the controller before adding editor mutation. |
| `renderer/mobys/MobyInstanceController.ts`, `MobyData.ts`, `MobyGltfSupport.ts` | Vanilla class projection, mission-independent UYA transforms, lighting, metal handling, and instance updates. | Three.js nodes/TSL, generated SDK-shaped types, model worker, DL mission helper. | Define Forge UYA DTOs, remove DL mission logic, split the 913-line controller, and attach stable Entity IDs to render instances. |
| `renderer/ModelPrimitiveMerge.ts`, `StorageInstancedMesh.ts`; `renderer/model-workers/ModelSourceLoader.ts`, `ModelSource.worker.ts` | Primitive compatibility checks and off-main-thread glTF decode/hydration. | Three.js, GLTFLoader, Web Workers; storage instancing is backend-sensitive. | Benchmark worker benefit; add abort and pool disposal; dispose superseded merge geometry; select ordinary or storage instance attributes per winning backend. |
| `renderer/ties/TiePrimitiveMerge.ts` | Preserves alpha component ordering while reducing primitives. | Three.js geometry types and tie metadata. | Retain its tests, but run it only after ownership is explicit and record allocation/draw-call deltas. |

The old `tiePackageParsers.ts`, `shrubPackageParsers.ts`, and gameplay normalization
inside `mapLoadPipeline.ts` are reference material for the host-side package
contract, not TypeScript to port. Their raw UYA record knowledge belongs in the
.NET host/SDK.

### Leave behind

| Source area | Reason |
| --- | --- |
| `features/map-loader/**`, `MapViewerScreen.tsx`, `mapViewerState.ts`, and all viewer/debug windows | React state, localStorage, routing, and monolithic callback wiring conflict with Forge's runtime, docking, settings, selection, and command boundaries. |
| `services/mapLoading/**`, `services/sdk/**`, `services/renderPackages/indexedDbRenderPackageStore.ts`, `fetchMapSourceBytes.ts` | Browser downloading, generated TypeScript SDK workers, gameplay duplication, and IndexedDB caching are replaced by the .NET host and Forge asset store. |
| `services/customMaps/**`, map catalogs, Vite preview-WAD plugins, and `fflate` | Network catalogs, custom ZIPs, and non-UYA preview tooling are not part of the M0 walking skeleton. |
| `renderer/mobys/simulation/**` and `MobySimulationController.ts` | Game simulation is not required for an editor viewport. Reintroduce only a specific visual/tool need, starting with UYA, after the static scene is stable. |
| `renderer/rc1/**` and GC/DL-specific branches | P0 targets NTSC-U UYA only. |
| `TightBloomNode.ts` | Keep only in the benchmark if needed for equivalent effects; do not migrate it to production unless the chosen reference scene requires bloom for accepted visual correctness. |
| Asset preview windows and their separate renderers | They duplicate render loops and resource ownership; later asset panels should reuse Forge's preview service. |

## Known problems to fix, not copy

### Allocation and memory

- The browser converter makes four full `Uint8Array.from(WAD)` copies, one for
  each SDK worker, while each worker creates a package part and the main thread
  allocates another merged package. Forge's .NET host path eliminates this.
- IndexedDB structured cloning, packed-package retention, Blob creation, glTF
  decoding, and GPU upload can temporarily retain several representations of the
  same map. Forge should expose package entries through an app-owned protocol or
  bounded temporary files instead of sending the full package repeatedly through
  host, main, preload, and renderer.
- `readPackedFileBytes(...).slice(...)` copies gameplay blocks. This path is not
  ported; host results should use explicit compact DTOs/buffers.
- Tfrag preparation clones every geometry, converts indexed geometry to
  non-indexed geometry, then merges it. That can multiply vertex memory. Preserve
  indices where material/attribute compatibility permits and measure the change.
- `mergeModelPrimitives` repeatedly searches prior primitives and re-merges growing
  buffers, creating quadratic work/copies in a large compatible group. Group once
  by render state, then merge each group once if the benchmark justifies merging.
- The tfrag atlas expands decoded images into a canvas plus a new texture, has no
  device-size cap, and leaves original texture ownership unclear. Query backend
  limits, fail to multiple atlases, and dispose source textures only after all
  consumers release them.

### Resource ownership and cancellation

- General, shrub, and skybox disposal releases geometry/material objects but not
  texture properties referenced by materials. Tie disposal covers only two
  generated user-data textures. Repeated loads can retain GPU textures.
- Multiple independent disposal helpers can double-dispose shared geometry or miss
  shared textures. Forge needs one reference-aware scene resource tracker.
- Instanced geometries reuse source attributes and then dispose the source scene.
  The existing WebGPU path already special-cases a Safari `buffer.destroy` failure;
  ownership must be made explicit before reuse.
- `PackedMapAssetPackage.dispose()` can run while an object-URL promise is pending;
  a URL created afterward is no longer in the cleared set and cannot be revoked.
- Renderer/component teardown marks React callbacks disposed but does not abort
  glTF requests, class loads, worker jobs, texture loads, or first-frame compile.
  Late work can target an already disposed renderer.
- The model worker pool is module-global and never terminated during scene/app
  disposal. Requests have no cancellation, and failed slots are immediately
  recreated even when no scene remains.
- `MapSceneRenderer.dispose()` does not null its renderer and controller ownership
  is split between controller cleanup and root traversal. Make disposal idempotent
  and invalidate every late async continuation.

### Draw calls and frame behavior

- Ties, shrubs, and mobys are instanced, spatially chunked at 2,400 units, and
  capped at 768 instances per chunk. This enables culling but produces one draw
  per class primitive, spatial chunk, mirror state, alpha/water pass, and material
  state. Record the actual breakdown on light and heavy UYA scenes.
- `BundleGroup` exists but is disabled by default. Enabling it disables per-mesh
  frustum culling and needs explicit invalidation after edits, so it is not a free
  optimization for an editor.
- Tfrag material/atlas batching reduces draws but trades startup CPU time and
  memory for them. Keep it only when measured totals improve.
- Sky and scene use separate passes; bloom adds MRT attachments and another
  pipeline. Benchmark with the same effects and resolution on both backends.
- The frame limiter still receives the renderer animation callback and skips
  submissions. This is acceptable, but performance reports must distinguish
  callback frame time from submitted GPU frames.

### Backend and maintainability coupling

- Nearly every renderer file imports `three/webgpu`; materials use TSL,
  `StorageInstancedBufferAttribute`, `BundleGroup`, MRT passes, and
  `RenderPipeline`. The current availability check rejects WebGL even though
  Three 0.184 supports the same node renderer with `forceWebGL: true`.
- WebGPU compatibility handling contains browser-specific error-string matching.
  The production backend must instead expose a small typed initialization/device
  loss boundary selected after M0-007.
- The largest modules are `MapSceneRenderer` (1,595 lines),
  `TieInstanceController` (1,294), `TfragMaterialController` (1,098),
  `MobyInstanceController` (913), and `ModelMaterialNodes` (739). None should be
  copied intact under Forge's roughly 500-line class/module guideline.
- Package manifests are cast from JSON without runtime validation and tolerate
  unknown/malformed values inconsistently. The Forge package boundary is a trust
  boundary and must validate schema version, entry paths, offsets, and required
  fields before allocating scene resources.

## Migration order

Every step keeps the current Forge cube or the last working scene available; no
step lands a half-wired replacement.

1. **Benchmark in the source viewer (M0-007).** Add a temporary backend choice to
   the existing TSL renderer: normal WebGPU versus `forceWebGL: true`. Use the same
   package, camera, resolution, warm-up, effects, and statistics. Choose one and
   delete the losing production path.
2. **Freeze the Forge render-package boundary.** Add a versioned UYA package/DTO
   fixture produced by the .NET host and an Electron `forge-package:` reader.
   Validate it without changing the cube viewport.
3. **Port a cancellable scene shell.** Bring over camera framing, resize/frame
   lifecycle, selected backend creation, and one resource tracker. Render the cube
   through that shell; verify repeated mount/unmount reaches baseline memory.
4. **Add terrain.** Adapt package manifests and tfrag preparation/materials. Keep
   the cube fallback and guided missing-package state. Record allocations and draw
   calls before enabling atlas/non-indexing optimizations.
5. **Add the skybox.** Port UYA shell ordering/animation only, with explicit texture
   ownership. The terrain-only scene remains valid when sky data is absent.
6. **Add ties, then shrubs, then mobys as separate commits.** Each family consumes
   host-normalized records, attaches Entity IDs, owns its resources, and has a
   visible toggle. Each preceding scene remains runnable if the next family is
   absent or fails validation.
7. **Add model-worker parsing only if measured.** Start with GLTFLoader on the
   renderer thread plus progress/yields. Introduce a bounded disposable worker
   pool only if the representative heavy scene shows a material interaction win.
8. **Package and soak (M0-008).** Load a local non-proprietary or user-supplied UYA
   package without a dev server; repeatedly load/unload it while checking renderer
   memory, object URLs, worker count, and host/main/renderer copies.

## Verification record

- Source `npm test`: 26/26 passing on 2026-09-19.
- Source `npm run build`: passing on 2026-09-19.
- Static tracing covered React entry/lifecycle, WAD/package conversion, IndexedDB,
  package readers, SDK/model workers, all scene-family controllers, render passes,
  and disposal helpers.
- M0-007 must add raw benchmark results and a backend ADR before production
  renderer code is migrated.

## Consequences

- Forge retains the viewer's hard-won UYA visual knowledge without retaining its
  browser persistence or SDK duplication.
- The first migration takes longer than copying `MapSceneRenderer`, but gives the
  editor cancellation, Entity-ID projection, and deterministic ownership where
  later selection and mutation need them.
- Non-UYA rendering and game simulation remain available as reference material,
  not accidental P0 scope.
