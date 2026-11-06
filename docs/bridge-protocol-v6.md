# Forge binary bridge protocol v6

Protocol v6 extends the [v5 payloads](bridge-protocol-v5.md) with exact asset-source
selection and a renderer sound-state revision. Electron and Forge.Host must both
use v6; mismatched versions are rejected during the handshake. Frame structure,
progress, and cancellation are unchanged. The additive source-level opcode below
uses the existing request/result framing.

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

Opcode 47 (`ListSourceLevels`) accepts the source game (`RC1`, `GC`, `UYA`, or `DL`)
and configured clean ISO path as strings. It returns a count-prefixed list of
level IDs (`u32`), discovered through the SDK's game-neutral level catalog.
IDs are game level IDs, including GC museum level 30, rather than disc table
indices. Unknown games and trailing payload bytes are rejected. Electron formats
the IDs with game-specific display names for the map-content donor selector.

Content-plan requests (opcode 36) optionally append a source-game string after
the selection list. Omission retains the existing UYA request bytes and behavior;
`GC` selects the verified GC NTSC-U 1.01 donor, and `RC1` selects RC1 NTSC-U 1.00,
independently of the UYA target project's source fingerprint. Other source games
are rejected for planning.
The plan's game, region, and revision identify the donor. Target project ID,
fingerprint, and level continue to identify the active UYA workspace.

GC planning inventories gameplay instances and geometry, primary and streamed
terrain and collision, sky shells, settings, lights, groups, and singleton banks
through the SDK. It uses the UYA target's replacement and protection rules and
does not modify the project or catalog. GC families support replacement mode.
Apply requests (opcode 39) optionally append the same source-game string after
the reference choices. Existing UYA requests retain their original bytes.

GC apply revalidates the donor and target against the approved batch, converts
model dependencies through the SDK, and commits all selected families in one
transaction. Imported entities and batch records retain GC provenance. PVar
bytes, native Moby links, and relative pointers are preserved; UYA class-specific
PVar schemas and behavior are not applied to GC data. An attached World blob
also preserves the complete original GC gameplay payload, including unused PVars
and the original link tables. Undo/redo and save/reopen include these attachments.
GC settings replacement updates the shared color/fog fields while preserving
the target's UYA scene settings and their references.
RC1 uses the same replacement transaction, dependency attachment, original-gameplay
archive, and donor-provenance contract. The SDK converts RC1's archive layout,
inline TIE ambient colors, Moby records, sky headers, and level settings to UYA
representations. The SDK converts RC1 terrain textures to UYA's supported base,
half-size, and quarter-size mip chain. RC1 ship paths and camera cuboids require preview mappings
when their donor geometry is skipped; selecting that geometry maps them automatically.
GC inventory reads every catalog page and counts the resident terrain buffer once.
Occlusion accepts sparse and unused authored mappings; native replacement expands
TIE mapping slots in instance order and preserves streamed terrain's visibility links.
