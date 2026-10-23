# UYA content-import capability contract v1

Status: M16-001 through M16-008 implemented; M16-009 packaged and runtime qualification
in progress (2026-10-21)

This contract freezes how the UYA adapter describes donor-map import families before
any UI enables them. It separates the family's intended import mode from implementation
readiness: a planned `add+replace` family remains disabled until its reader, project
representation, dependency closure, writer, transaction, and qualification evidence
are all complete.

## Versioned adapter matrix

`ContentImportCapabilitySchema.CurrentVersion` is `1`. The game adapter returns one
`ContentImportCapabilityMatrix` for target `(game, region, revision)` and one stable
entry for each family below. Electron and React consume the serialized matrix; they do
not infer support from visible entities, catalog rows, or renderer code.

| Family ID | Intended mode | Default when ready | Current state | Owning task/blocker |
| --- | --- | --- | --- | --- |
| `ties` | add+replace | Yes | Ready | M16-004 donor attachment/conflict remap; M16-008 owns UI |
| `shrubs` | add+replace | Yes | Ready | M16-004 class allocation/attachment; M16-008 owns UI |
| `tfrags` | add+replace | Yes | Ready | M16-005 attached primary/chunk donor-owner composition; M16-008 owns UI |
| `mobys` | add+replace | No | Ready | M16-007 dependency-complete import; M16-008 explicit external-reference mapping UI |
| `collision` | add+replace | No | Ready | M16-006 complete solid/player-barrier composition and portable donor payload |
| `cuboids` | add+replace | No | Ready | M16-006 complete shape/camera-collision composition; M16-008 owns UI |
| `spheres` | add+replace | No | Ready | M16-006 complete shape/camera-collision composition; M16-008 owns UI |
| `splines` | add+replace | No | Ready | M16-006 complete variable-record composition and inbound-reference protection |
| `paths` | add+replace | No | Ready | M16-006 complete grind-path composition and inbound-reference protection |
| `areas` | add+replace | No | Ready | M16-006 complete section composition and explicit cross-family reference remap |
| `mapGroups` | replace | No | Ready | Stable member IDs remap to final native indexes after whole member-family replacement |
| `skybox` | replace | No | Ready | M16-008 atomic whole-composition replacement/provenance |
| `levelSettings` | replace | No | Ready | M16-008 target-native patching/reference rebinding |
| `directionalLights` | replace | No | Ready | M16-008 whole-array replacement/tie selector validation |
| `hud` | replace | No | Ready | Whole header/bank attachment and deterministic HUD composition |
| `fx` | replace | No | Ready | Whole-bank attached textures and SDK asset-WAD relocation |
| `occlusion` | replace | No | Ready | Whole donor mapping/grid/level payload; requires complete TIE/Moby/tfrag replacement |
| `opaqueContent` | unsupported | No | Hidden | Dependency-safe remap is unprovable |

The runtime entry carries `implementationReady`, `selectable`,
`defaultSelectedWhenReady`, and `defaultSelected` independently. `defaultSelected` can
be true only when the family is implementation-ready, the donor inventory is present
and valid, and the family is one of TIE/shrub/tfrag. Completing an implementation task
changes readiness and evidence; it does not silently change the versioned mode.
Unsupported opaque content remains in the host contract for compatibility and inventory
accounting, but Forge does not present it as an import action.

## Source inventory states

M16-002 supplies one bounded source state per discovered family:

- `unknown`: inventory has not run; the family is disabled;
- `absent`: the donor has no family payload; the family is disabled without treating
  the map as malformed;
- `empty`: a structurally valid empty family; the family is disabled because there is
  nothing to import;
- `present`: content exists and may become selectable when implementation-ready; and
- `malformed`: the family is disabled with the bounded parser diagnostic.

Unknown family IDs, duplicate inventory entries, invalid states, malformed entries
without a diagnostic, and unsupported target profiles fail at the adapter boundary.
The v1 writable target is UYA NTSC-U revision 1.00.

## Enabled-family contract fields

Every non-unsupported entry defines and serializes:

- native/project ownership;
- transitive dependency edge kinds;
- conflict namespaces and the indexes/IDs requiring final-order remap;
- add/remove/replace native composition semantics;
- the protected-survivor rule;
- the no-edit identity promise;
- native/project limits; and
- concrete reader, representation, writer, or qualification evidence.

The checked-in adapter values are authoritative and intentionally conservative. The
important boundaries are:

- Static TIE/shrub definitions own model/material/texture data; instances additionally
  own transforms, attached collision, groups, occlusion/ambient state, and typed links.
  Replacing an instance removes exclusively attached collision and detaches it from shared
  collision pieces. Class conflicts are resolved only after content identity is known.
- A tfrag selection carries complete native primary or streamed owner groups, all
  three LODs, texture/material records, and occlusion mappings. The project-local
  custom-tfrag path proves primary-group generation but is not donor-owner import.
- Mobys retain raw instance/PVar bytes and require final entity, PVar, model, sound,
  sample, and remap ordering. Native Moby-link relocation entries are promoted to
  stable typed references even when MobyDex has no field name; M15 sound closure is
  mandatory.
- Collision never infers ownership from proximity to terrain. Its raw types (including
  material/sound semantics), solid faces, player barriers, and explicit links travel
  together in attached donor payloads. Final composition rebuilds native indexes and
  octants; links map only to TIE/shrub instances selected in the same import.
- Cuboids, spheres, splines, grind paths, and areas have complete project records,
  inbound-reference protection, and structural section composition, so M16-006 enables
  them for explicit add/replace plans. Shape composition also rebuilds camera-collision
  indexes. Project schema 24 retains spline/path native record headers, and the SDK
  rebuilds variable offsets, points, bounds, and metadata after final ordering. Area
  composition rewrites every spline/shape list to final native indexes; links to
  selected donor geometry map automatically, while unselected nullable links require
  an explicit retained-target mapping or clear choice. Map-group replacement stores
  ordered stable member IDs and rebuilds native indexes after final Moby/TIE/shrub
  ordering; every represented member family must be replaced in the same batch.
  Project schema 25 persists these authoritative tables and migrates older projects
  with none, preserving their existing opaque base tables.
- Skybox, level settings, and directional lights are replace-only singletons/families.
  Skybox replacement now swaps the complete shell composition, attaches the donor
  payload, validates a semantic recompose, and blocks protected target shells before
  commit. Level-settings replacement patches the target-native record and automatically
  maps selected donor cuboids while requiring explicit Map/Clear choices for external
  ship-path/camera links. Directional-light replacement rebuilds the complete ordered
  array (up to its 16 addressable slots) and rejects the plan unless every retained or
  imported TIE's packed primary/secondary selector remains valid.
- HUD replacement attaches the complete verified donor header/five-bank source plus
  normalized preview textures, then uses that portable payload as the HUD author's
  source. It is replace-only and requires an existing target HUD container.
- FX replacement attaches every valid normalized donor texture, preserves contiguous
  donor indexes, and rebuilds the bank at the donor's exact count. The UYA SDK composer
  relocates the surrounding target asset-WAD payloads, so shrinking or growing FX does
  not replace unrelated terrain, models, sky, or collision. It is replace-only and
  requires an existing target FX bank. Opaque content remains unavailable because its
  dependencies cannot be classified or safely remapped.
- Occlusion replacement attaches the donor gameplay mappings, asset-WAD spatial grid,
  and level-WAD payload as one portable source. It is replace-only and requires complete
  TIE, Moby, and tfrag replacement so visibility-bit meanings cannot cross maps.
  Project schema 26 persists the attached source authority.

## Bridge/UI contract

Bridge opcode `GetContentImportCapabilities` accepts a target profile plus optional
family inventory states and returns the full versioned adapter matrix. Both C# and
TypeScript codecs bound the family count, validate enum values, reject trailing data,
and preserve every reason and dependency field. M16-002's `PlanContentImport` operation
combines this response with verified donor inventory and a target snapshot; renderer
conditionals cannot promote a family. The plan contract is recorded in
[UYA content-import planning v1](uya-content-import-planning-v1.md).

## Verification

Synthetic protocol fixtures cover the complete ordered family snapshot, intended
modes/defaults, required contract fields, unsupported families, unknown/absent/empty/
present/malformed source states, target rejection, deterministic bridge bytes, and C#
to TypeScript codec parity. M16-002 now supplies structural source inventory and
dry-run fixtures through the linked planning contract; proprietary donor payloads are
not stored in the repository. The ordered adapter payload is additionally locked by the
SHA-256 recorded in the [M16 qualification record](../qualification/M16-cross-map-import.md).
