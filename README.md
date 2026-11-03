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
Settings shows both source paths and links to Setup for source and import management.

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

The companion Ratchet SDK changes must be released and `ratchet-sdk.version`
updated before this feature can be built from a fresh pinned dependency checkout.
Local development can use the modified sibling SDK through the existing
`.dependencies/ratchet-sdk-root` configuration.

## Project planning

- [Product and technical specification](docs/forge-v2-spec.md)
- [Binary bridge protocol v6](docs/bridge-protocol-v6.md)
- [Identity and project schema v0](docs/identity-and-schema-v0.md)
- [UI guidelines](docs/ui-guidelines.md)
- [Milestones and task registers](docs/planning/README.md)
