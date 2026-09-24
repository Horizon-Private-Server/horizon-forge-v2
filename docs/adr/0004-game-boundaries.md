# ADR 0004: Game-neutral core and target-game adapters

Status: Accepted

## Context

Forge currently targets UYA NTSC-U, but later releases will support additional Ratchet & Clank games. Shared editor code must not acquire UYA assumptions, while format-specific code must remain explicit enough to audit during lossless rebuilds.

## Decision

Code is shared only when its inputs, outputs, dependencies, and diagnostics are independent of a game format.

| Area | Game-neutral | Target-game-specific |
| --- | --- | --- |
| Ratchet PS2 libraries | WAD compression, PIF decoding, indexed-texture inventory and palette optimization, common geometry and asset primitives | Binary layouts, constants, readers, writers, and archive rules in `RatchetPs2.Games.<game>` |
| Ratchet PS2 SDK | Game-neutral entry points that dispatch from `GameId` or equivalent target metadata | No public target-game implementations; game-specific archive, asset, and ISO rules live in the matching game project |
| Forge host | Project persistence, asset identity/catalog, editor runtime/history, bake graph, staging, validation primitives, and opaque-content storage under `Forge.Host.Domain` | Base-level import, canonical asset translation, game layer stores/writers, archive packing, and ISO validation/patching under `Forge.Host.Games.<game>` |
| Electron and renderer | Docking, scene interaction, build UI, progress, and renderer-facing API names | Dispatch to the game adapter selected by project metadata; no binary parsing in these layers |

The indexed-texture inventory, palette optimizer, and palette writer are in `RatchetPs2.Core.Textures.Palettes`. Forge's palette bake report is also neutral. UYA staging discovery remains in `UyaTextureInventoryService`, which adapts UYA files into the shared texture inputs.

Per-game frontend package builders remain public under `RatchetPs2.Games.<game>.Builders` because the TypeScript generator uses each one as an isolated browser-package root. Desktop code calls the game-neutral SDK facade instead. The builders currently reuse the established DL render pipeline where the binary formats overlap; that pipeline should move only when another implementation proves a clean shared boundary.

## Adding another game

1. Add its binary readers and writers under `RatchetPs2.Games.<game>`.
2. Add game-prefixed archive and disc implementations under `RatchetPs2.Games.<game>/Builders`, then extend the game-neutral SDK dispatch points.
3. Add Forge adapters for import, render preparation, bake, pack, and patch.
4. Reuse the project, editor, staging, opaque-content, and palette infrastructure unchanged.
5. Dispatch from project target metadata at the host boundary; keep the React API neutral.

Do not add a game factory or common interface until a second implementation exists. The current bake dependency graph may remain shared until another game proves that its topology differs. UYA canonical assets and frontend asset packaging remain UYA-specific because their source model formats and export profiles are currently UYA-defined.
