# Forge binary bridge protocol v6

Protocol v6 extends the [v5 payloads](bridge-protocol-v5.md) with exact asset-source
selection and a renderer sound-state revision. Electron and Forge.Host must both
use v6; mismatched versions are rejected during the handshake. Frame structure,
opcodes, progress, and cancellation are unchanged.

Within an editor placement command, the placement transform is followed by a
source-present boolean. When present, the source contains game, region, revision,
level, and archive strings, followed by a source index (`u32`). The host validates
that this identity belongs to the selected catalog asset before conversion.

Editor snapshots append a sound-revision string after diagnostics. This token is
opaque and scoped to the host session. Sound edits, placement imports, and undo/redo
update it; scene selection and unrelated edits retain it. Sound panels refresh
their inventories and discard decoded previews when this revision changes.

Moby placement retains the chosen source in its project sound map, including an
explicit empty map for a source without sounds. Different levels can share the
same model asset while using different sounds. All instances of one oClass share
one sound mapping; changing that mapping requires removing the old instances.
Deleted variants release their class allocation when another variant is imported,
and undo restores the previous allocation and sound map together.

Composed desktop preview banks are reused while the project's sound state remains
unchanged. Preview allocation limits are separate from the PS2 SPU packing limit;
packing continues to omit unused imported programs and samples.

[Interoperability vectors](../tests/fixtures/bridge-v6.json) cover v6 framing.

GC asset sources use the existing game-neutral setup opcodes with game `GC`.
The host advertises `gc.iso.validate` and `gc.assets.import` plus the supported
model families. GC NTSC-U 1.01 models use the `HFGC01` canonical header and retain
GC provenance; preview dispatches to GC and UYA placement attaches a converted
native copy through the existing undo/redo and bake flow. GC skies and legacy
primary/chunk sound banks use the shared catalog models. Framing and payload
layouts are unchanged.

RC1 sources use these same setup opcodes with game `RC1`, advertised by
`rc1.iso.validate` and `rc1.assets.import` with the supported model families.
RC1 NTSC-U 1.00 models use the `HFRC11` canonical header. Their source provenance
and class IDs survive conversion into project-local UYA assets. Extraction,
preview, placement, sound mapping, history, and baking use the existing payloads.
