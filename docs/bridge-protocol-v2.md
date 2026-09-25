# Horizon Forge bridge protocol v2

Version two retains the framing, byte order, limits, message kinds, opcodes, and
error codes from [protocol v1](bridge-protocol-v1.md). The header version field is
`2`; v1 and v2 peers reject each other during startup rather than decoding a
different editor snapshot layout.

## Editor entity snapshot delta

After the optional source class ID, each encoded editor entity now contains:

```text
bool hasGeometry
if hasGeometry:
  uint32 geometryKind  // 1 cuboid, 2 spline, 3 area, 4 sphere, 5 cylinder,
                       // 6 pill, 7 grind path, 8 directional light,
                       // 9 point light, 10 environment sample,
                       // 11 environment transition, 12 camera,
                       // 13 ambient sound
  uint32 pointCount
  (float32 x, float32 y, float32 z, float32 w)[pointCount]
bool dirty
bool hidden
bool disabled
bool locked
bool readOnly
bool invalid
bool missingAsset
```

Splines, grind paths, and directional lights carry points; other overlays use the
entity transform. Counts remain bounded by the protocol's entity limit. `readOnly`
is a derived capability flag and is never accepted as authority for host mutations.

The language-neutral framing vectors in
[`tests/fixtures/bridge-v2.json`](../tests/fixtures/bridge-v2.json) cover the v2
header. Project geometry and migration are defined in
[Forge project format v2](project-format-v2.md).
