# Forge project format v2

Version two extends [version one](project-format-v1.md) with optional typed editor
geometry and lighting on project entities. It does not change Asset IDs or copy
retail payloads into the project content document.

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

The original UYA gameplay blocks remain immutable opaque content and the sole bake
authority. Decoded geometry is read-only until a verified native writer explicitly
replaces that ownership. Renderer snapshots contain only the geometry kind and
path points needed for volume, path, and area overlays.
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
range, native matrix/inverse rotation, Euler rotation, padding, and raw record. Both
are read-only views; their original gameplay blocks remain bake authority.

## Migration

Both documents use `schemaVersion: 2`. Version-zero and version-one documents are
migrated in memory and are written only through the existing explicit journaled save
path. UYA base entity version `7` requires a verified source-ISO-backed upgrade to add
the supported volume, path, area, lighting, environment, per-tie ambient, camera, and
ambient-sound records plus camera-collision metadata while preserving existing Entity
IDs and user deletions. Area links are remapped to the retained Entity IDs during that
upgrade.
