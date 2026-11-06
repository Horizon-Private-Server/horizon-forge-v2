# horizon-forge-v2
v2 version of horizon forge, completely different architecture not based in unity.

## Development

Requires Node.js 22+, npm, and the .NET 10 SDK.

```sh
npm install
bash scripts/bootstrap-ratchet-sdk.sh
npm run dev
```

For an existing checkout at the pinned revision, use
`bash scripts/bootstrap-ratchet-sdk.sh --source /path/to/ratchet-ps2-cli`.
The `ratchet-sdk.version` selector accepts either an exact commit SHA or a release
tag such as `v0.4.4`. Publish and tag ratchet-ps2-cli first, update that file to
the new tag, then build or publish Forge.

`npm run build` verifies the Electron/React application and the .NET host.
Renderer and test typechecks reuse TypeScript's incremental cache under
`node_modules/.cache/forge/`. Remove that directory to force fresh checks;
Electron JavaScript compilation still runs on every desktop build.
`npm test` runs the protocol, identity/schema, host lifecycle, and desktop security checks.
`npm run package:linux` creates a self-contained Linux x64 app in `artifacts/`.
Pull requests and pushes run the locked Linux/Windows build matrix in GitHub Actions.

## Asset setup

Setup accepts an optional clean NTSC-U Deadlocked ISO for importing mobys
(including mission classes and sound banks), ties, shrubs, textures, and sky shells into the shared
catalog. DL imports support progress, cancellation, resume, and re-import. They
require no DL development ISO or map project. UYA remains the supported map target.
Setup has separate source and import controls for each game. Optional DL imports
can be resumed independently and do not block finishing UYA setup or using Forge.
Settings shows the configured source paths and links to Setup for source and import management.

DL ties, shrubs, and mobys can be dragged into UYA NTSC-U 1.00 projects. Placement
converts a project-local copy to UYA and retains the DL source. Ties and shrubs
receive a free class ID; mobys preserve their DL oClass, with conflicting project
classes reported before placement. Moby animations, sound definitions, animation sound triggers, and referenced
sound programs/samples are imported. DL sky shells can also be previewed and added
to UYA skies; conversion preserves their textures and loader rotation adjustments.
Game behavior code is not ported.
The Asset Preview source selector controls both explorer dragging and placement
at the origin. Moby choices include the source level's sound mapping, even when
several levels share identical model bytes. The choice survives saving and reopening.
Remove existing instances before importing a different variant of the same oClass;
all instances of that class share one model and sound mapping. Filtering the explorer
updates its active preview to match the variants available for dragging.

The Sounds dock refreshes its inventory and decoded previews when project sounds
change. Composed preview banks are cached across unrelated edits and repeated
placements of the same source. Desktop preview capacity is separate from the
PS2 SPU limit, which remains enforced when packing a map.

Texture conversion de-swizzles the base and half-size images while retaining the
linear quarter-size mip from the GS palette bank. Unsupported swizzled mip sizes
are omitted.
Models with unsupported animation data report a conversion error without changing
the project.

Opening a project repairs the initial DL tie normal-pointer conversion and
incorrectly de-swizzled or omitted quarter-size mips. Save the project, rebuild
occlusion, and rebuild the map to use the corrected assets; instances and
independently edited models/textures are retained.

Re-import DL assets in Setup to add sounds and sky shells to an existing catalog.
Reopening a project then imports available sounds for previously placed DL mobys
and repairs the original silent conversions. Save and rebuild the map afterward.
Opening a project also restores DL moby oClass IDs assigned to the old custom
range, updating their instances and sound maps together while retaining model edits.

Going Commando follows the same setup, catalog, preview, and UYA placement flow.
The supported source is clean GC NTSC-U revision 1.01 (`SCUS-97268`, MD5
`3cbbb5127ee8a0be93ef0876f7781ee8`). Import includes all 27 levels, including the
museum, model textures, skies, and primary/chunk sound banks. GC assets retain their
source class IDs; mobys retain their animations and supported
sound programs. Unsupported legacy sound arguments remain unavailable with a
specific diagnostic. UYA remains the map target; game behavior code is not ported.
GC tie conversion sets the packet setup offsets required by UYA's VU program and
uses the SDK's source-facing analysis to populate strip winding flags, retaining
each packet's `bfcDistance`. Strips with conflicting triangle facing are split
into consistently oriented runs with even vertex counts for UYA's paired VU loop.
Uniform-facing culled strips with odd vertex counts also use that padding path.
Rebuilt packets also regenerate the VU upload groups and their address-based stop
markers, including the prefetched vertices needed by short groups. Coincident
and coplanar overlapping faces do not form adjacency edges that reverse their
facing. Conversion retains established upload boundaries and adds splits when
corrected facing requires them.
Conversion preserves every rendered face, lighting recipe, glow target, material,
and texture-matrix UV pass, rebuilding packet and color-cache tables as needed.
Reopening a project repairs known earlier GC conversions and restores source class
IDs together with their instances. Independent model edits and class conflicts are
preserved and reported. Save and rebuild occlusion and the map after a repair.

GC asset support is included in the pinned SDK release. Bootstrap uses the clean
SDK checkout directly and refuses local SDK edits.
The focused SDK GC, RC1, sound-bank, and DL conversion regressions run as part of `npm test` and CI.


Ratchet & Clank (RC1) follows the same optional setup, catalog, preview, and UYA
placement flow. The supported source is clean NTSC-U revision 1.00 (`SCUS-97199`,
MD5 `a62a8ae0e44c2580be38c82f48b7d181`). Import covers all 19 levels, including
model textures, sky shells, embedded sound banks, and moby sound mappings. RC1
models retain their source class IDs. Moby vertex headers are repacked for UYA;
TIE conversion rebuilds all LOD packets, lighting palettes and remaps, winding,
and VU upload groups while retaining backface culling. Sky shell headers are
translated to UYA's layout. Game behavior code is not ported.
RC1 TIE materials using transparent pixels render both sides; mixed packets split
to retain culling on opaque geometry. Existing placements update when the project
opens; save and rebuild occlusion and the map to apply the correction in-game.

RC1 textures retain their authored mip chain (RC1 stores an explicit level count,
with the smallest levels in GS palette memory). UYA materials keep the base,
half-size and quarter-size images. After upgrading an earlier RC1 import,
re-import RC1 in Setup, reopen and save existing projects, and rebuild the map.
Existing placements recover the missing mips while preserving model and texture edits.

Models exceeding UYA's packet or lighting limits report a conversion error before
changing the project. Oversized RC1 models such as TIE `0x05DE` are split into native UYA Ties during map-content importing,
retaining all three LODs, lighting, map groups, and occlusion. Preview counts the resulting
placements and reports the split. Individual asset placement still requires one native TIE.
Publish and tag the matching SDK changes and update `ratchet-sdk.version`
before publishing Forge; development builds can use the sibling SDK checkout.
GC, RC1, and DL map-content importing also require the SDK's `LevelContentInventoryReader`
and `LevelContentConverter` entry points. Select a donor game in Import map content,
preview the selected replacement families, and apply them to the active UYA project
through one undoable transaction. Mobys retain raw PVars and native link data;
donor gameplay behavior is not translated into UYA. GC imports background/fog
settings while retaining the target scene. RC1 and DL also import their scene settings;
ship paths and camera cuboids map automatically when their geometry is selected,
otherwise preview requires an explicit target or clear choice.
Retail map-content availability can be checked without editing existing projects
with the protocol test runner's `--qualify-gc-content <gc-iso> <catalog-root>` option.
Use `--qualify-rc1-content <rc1-iso> <catalog-root>` for RC1 or
`--qualify-dl-content <dl-iso> <catalog-root>` for DL. DL imports the resident core
gameplay and world instances, including streamed terrain and collision; mission-specific
Moby instances and mission behavior are not merged into the target. Unconvertible model data blocks the affected family with a diagnostic before import.

## Project planning

- [Product and technical specification](docs/forge-v2-spec.md)
- [Binary bridge protocol v6](docs/bridge-protocol-v6.md)
- [Identity and project schema v0](docs/identity-and-schema-v0.md)
- [UI guidelines](docs/ui-guidelines.md)
- [Milestones and task registers](docs/planning/README.md)
