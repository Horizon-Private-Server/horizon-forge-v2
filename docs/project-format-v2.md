# Forge project format v3

Version three extends version two with editable project-level environment settings.
It does not change Asset IDs or copy retail payloads into the project content document.

## Typed source geometry

An entity's optional `geometry` object contains exactly one of:

- `cuboid`, `sphere`, `cylinder`, or `pill`: the 16 native matrix floats, 12
  inverse-rotation floats, and Euler rotation;
- `spline`: ordered four-component points, including the native `w` value;
- `grindPath`: its bounding sphere, integer metadata flags, and ordered
  four-component points; or
- `area`: the bounding sphere, last-update value, and typed membership lists.

Each area membership retains its source index and, when the target was decoded, its
stable Entity ID. Missing or out-of-range targets remain present with a null Entity
ID so Forge can diagnose them instead of dropping the relationship.

The retained UYA gameplay blocks remain bake authority. Shapes use verified
patch-in-place transform writers. Ordinary splines expose every native point component,
including `w`, and their writer supports point insertion, deletion, reordering, and
transforms while preserving record padding. Grind paths, areas, and unsupported fields
remain read-only. Renderer snapshots contain only the geometry kind and path points
needed for volume, path, and area overlays.
Shape records optionally carry camera-collision flags, integer/float parameters, and
the grid primitive's bounding sphere; the 64×64 lookup table remains derived data.

## Typed source lighting

An entity's optional `lighting` object contains exactly one directional light,
point light, environment sample point, or environment transition. Packed point-light
coordinates, radius, and 16-bit colors remain intact alongside their display
transform. Transition records retain their inverse matrix and endpoint lighting and
fog values. Each source tie carries its directional-light selector and variable-length
ambient RGBA words directly in `tieLighting`, so project ordering cannot change the
association.

Lighting views are read-only. The original target-native lighting assets remain bake
authority until verified writers replace them.

## Typed source cameras and sounds

Camera entities preserve their type, position, Euler rotation, Pvar index, and raw
record. Ambient sounds preserve class, mission class, update pointer, Pvar index,
range, native matrix/inverse rotation, Euler rotation, padding, and raw record. Their
transforms use patch-in-place native writers while structural edits remain unsupported.

## Level settings

The optional project-level `levelSettings` object owns background and fog colors,
distances, and intensities in editor units. The World bake layer patches those fields
into the retained native block and preserves every unsupported byte. Remaining world,
ship, chunk-plane, and sound-count fields stay read-only in the render snapshot.

## Migration

Both documents use `schemaVersion: 3`. Version-zero through version-two documents are
migrated in memory and are written only through the existing explicit journaled save
path. UYA base entity version `9` requires a verified source-ISO-backed upgrade to add
the supported volume, path, area, lighting, environment, per-tie ambient, camera, and
ambient-sound records, camera-collision metadata, and editable level settings while
preserving existing Entity IDs and user deletions. Area links are remapped to the
retained Entity IDs during that upgrade.

Cuboid, sphere, cylinder, pill, camera, and ambient-sound transforms have native
patch-in-place writers. Ordinary splines additionally support editing and resizing their
ordered `vec4` point arrays. Spline-instance insertion/deletion and camera-collision grid
regeneration remain unsupported.
