# UYA level-settings contract

Forge treats the verified prefix of `gameplay/core/level_settings.bin` as editable and
preserves the rest byte-for-byte. The SDK remains the authority for this binary layout;
the project stores editor units and stable Entity IDs.

| Offset | Field | Native representation | Forge representation |
| --- | --- | --- | --- |
| `0x00`–`0x17` | Background/fog colors | Three signed 32-bit channels each | RGB channels clamped to `0`–`255` on import |
| `0x18`–`0x24` | Fog distances/intensities | Four finite floats | Distances use world units (`native / 1024`); intensities stay `0`–`255` |
| `0x28` | Death height | Finite float | Native world units; no scale conversion |
| `0x2c` | Spherical-world state | Signed 32-bit integer | Boolean; writes canonical `0` or `1` |
| `0x30`–`0x47` | Sphere center and ship position | Two finite float vectors | Native world units; no scale conversion |
| `0x48` | Ship Z rotation | Finite float | Radians, without scale conversion |
| `0x4c` | Ship path | Signed 32-bit spline index | Nullable stable spline Entity ID |
| `0x50`–`0x57` | Ship camera start/end | Two signed 32-bit cuboid indexes | Nullable stable cuboid Entity IDs |

The link sentinel is `-1`; other negative values are invalid. Non-negative links address
the deterministic native order of core splines or cuboids. Forge resolves them to Entity
IDs on import and back to indexes during bake, so project list order cannot retarget a
link. A non-sentinel source index that cannot resolve remains diagnosable and blocks
bake rather than silently becoming `None`. Deleting a resolved target clears its
nullable setting to `-1` in the same undoable project mutation.

Bytes from `0x58` onward—including padding, chunk planes, counts, and trailing target
data—remain opaque and are copied from the verified source. A no-edit SDK write is
byte-identical, and field-specific tests assert that edits do not touch bytes outside
their documented ranges.

The viewport projects ship position as an `airplane-tilt` billboard and, while
spherical-world mode is enabled, sphere center as a `sphere` billboard. Both are
translate-only tool targets; dragging them commits the same undoable level-settings
command used by the property controls rather than creating scene entities.

Death height is shown as a viewport-only horizontal plane with transparent warning
stripes and repeating skull markers. Its extent follows loaded terrain and entity
positions with a bounded fallback, and the viewport Visibility menu can hide it without
changing project state. Like the position billboards, it is excluded from placement,
snapping, project serialization, and baked output.

Format evidence is consistent across the SDK reader, Replanetizer's UYA offsets, and
Wrench's `LevelSettings` model and `rewrite_level_settings_links`, where these values are
declared as `pathlink` and `cuboidlink` and remapped with instance ordering.
