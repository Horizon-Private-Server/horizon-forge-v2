# P0/MVP release boundary

P0 and MVP are the same release priority. P0 proves that Forge can safely complete
the entire authoring loop with vanilla NTSC-U Up Your Arsenal content.

## Included

- Linux-first Electron/React/Mantine/Three.js desktop application.
- Bundled self-contained .NET 10 host using a pinned manually bootstrapped Ratchet
  SDK revision.
- Versioned binary Electron-to-.NET bridge.
- Clean UYA ISO validation, global vanilla asset import, and separate development
  ISO creation.
- Portable projects created from UYA base levels.
- Scene tree, properties, selection, translate/rotate/scale, snapping, Page Down
  ground placement, keybindings, and bounded undo/redo.
- Vanilla tie, shrub, moby, sky, tfrag, gameplay, collision, and lighting support
  to the extent explicitly enabled by host capabilities.
- Byte-exact pass-through for unsupported data including code overlays.
- Incremental deterministic bake to staging, WAD build, recoverable development
  ISO patch, and manual reload in externally managed PCSX2.
- Lossless shared-palette optimization for compatible vanilla tie, shrub, and moby
  textures.
- Linux and Windows packages, nightly/stable GitHub publishing, update checking,
  diagnostics, documentation, and release qualification.

## Excluded

- Custom GLB import and PS2 model conversion.
- Custom texture quantization and the fidelity/VRAM slider.
- RC1, Going Commando, Deadlocked, PAL UYA, NTSC-J, or other writable/import
  pipelines.
- Direct PCSX2 launch, pause, reload, or PINE integration.
- Collaboration relay/client behavior, third-party plugins, macOS, and delta
  updates.
- Editing opaque code overlays or unknown WAD structures.

## Release gate

A clean supported UYA ISO can be configured without risk; a base project can be
created, edited, saved, recovered, baked, built, and patched; untouched/unsupported
content is preserved; the resulting development ISO reloads successfully in
PCSX2; and the same project passes packaged Linux and Windows smoke tests.

