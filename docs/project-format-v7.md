# Forge project format v7

Version seven adds sparse per-face raw-type overrides to reusable TIE collision
proxy bindings. It does not place geometry in project JSON or create a replacement
asset for each paint stroke.

Each `tieCollisionBindings` entry now contains an ordered `faceTypeOverrides` list.
An entry stores a zero-based `faceIndex` in the exact bound proxy Asset ID's decoded
solid-face order and the complete target-game `rawType` byte. Indexes are unique and
strictly increasing. Values equal to the recipe's default raw type are omitted.

Face identity never crosses a proxy replacement. Applying or regenerating a proxy
creates a binding with an empty override list rather than attempting a geometric
remap from the previous Asset ID.

Version-zero through version-six documents migrate in memory. Existing bindings
receive an empty override list, preserving their prior uniform recipe raw type. Files
are replaced only through the existing explicit journaled save path.
