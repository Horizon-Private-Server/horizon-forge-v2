# UYA ocean simulation v1

Status: corrected implementation candidate; awaiting M17-003 visual validation  
Class: UYA `0x0B37` (`2871`)

## Authoritative inputs

The simulator consumes the editor's typed MobyDex descriptor, never renderer-owned
byte offsets. Required paths are:

| Concern | MobyDex paths |
| --- | --- |
| FX layers | `overlayFxTexture`, `underlayFxTexture` |
| Wave shape | `waveSpeed`, `waveCrest`, `waveSurge`, `waveRippleSize`, `waveDirection`, `waveDirectionVariation`, `waveShimmerIntensity` |
| Overlay motion | `overlayTiling`, `overlayDirection`, `overlaySpeed` |
| Color/blend | `underlayColor`, `overlayColor`, `overlayAdditive` |
| Water fog | `fogColor`, `fogIntensityNear`, `fogIntensityFar`, `fogNearDistance`, `fogFarDistance` |
| Surface | `waterShimmerThreshold`, `zPosition` |
| Camera hiding | `hideWhenCameraInCuboid0`, `hideWhenCameraInCuboid1` |

Values marked invalid by MobyDex are rejected with the owner field named. Reference
fields use resolved stable Entity IDs and immutable cuboid transforms. Ocean PVar FX IDs
are level-relative and resolve from UYA bank index `0x64 + id` through the current
project FX state, so source textures, overrides, and appended
textures share the normal project Asset-ID preview path. A missing/undecodable overlay
is skipped with an entity diagnostic; a solid valid underlay may continue. If no valid
layer remains, the ordinary Moby proxy remains visible.

## Rendering behavior

- One class simulator groups every `0x0B37` instance and shares each referenced FX
  texture load. Each instance owns one bounded 32-by-105 camera-relative patch (6,720
  triangles); its underlay and overlay share that geometry.
- The eight verified directional wave components drive vertex height and shimmer.
  Overlay UVs use world-space tiling, direction, speed, and deterministic fixed time.
  Textured underlays evaluate the wave normal and the reference projected STQ lookup
  per fragment. Underlay lookup textures use direct linear sampling without mipmaps.
- PVar colors use PS2 alpha `0x80` as full opacity. Each pass snapshots the current
  RGBA16F composer framebuffer into a format-compatible half-float texture, performs
  normal fixed-alpha or additive blending in the PS2 encoded-color domain, and decodes
  the result for Forge's linear render target. Both layers apply the PVar water-fog range
  and intensity. M17-005 retains the packaged comparison record and broader
  representative-map matrix.
- The surface uses PVar `zPosition`, follows the camera horizontally, is hidden below
  the surface or inside either resolved hide cuboid, and skips hidden-frame geometry
  updates. Camera-relative geometry and view shimmer update after camera controls for
  the frame, so rendering never uses a stale patch basis. Pause freezes time; reset
  returns time/scroll/waves to zero.

## Editor behavior and ownership

Derived meshes bind to the authoritative Moby Entity ID. Picking the surface therefore
selects the same Scene/Properties record; no derived entity enters serialization,
history, collision, build fingerprints, or bake. The ordinary proxy is suppressed only
while a valid simulator owns it. Entity hide/disable/lock rules still govern visibility
and picking. Selection outlines never duplicate the ocean patch.

The simulator owns patch geometry, shader materials, decoded Three.js textures, and FX
leases. Toggle-off, failure, invalidation, project switch, graphics-context loss, and
viewport disposal release them and restore the proxy. PVar/reference/FX/entity changes
recreate only the registered ocean class; unrelated classes and scene families remain.
