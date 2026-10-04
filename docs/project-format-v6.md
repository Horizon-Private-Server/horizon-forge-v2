# Forge project format v6

Version six can attach an imported solid-collision component to the exact TIE entity
that most likely owned it in the source level. The attachment stores the TIE Entity
ID and its transform at import time; it does not add metadata to the game collision
payload or replace the authored collision geometry.

During source import, Forge compares each solid component against TIE geometry in
instance-local space. Components with strong bounds containment are ranked by coverage
and center alignment; the best candidate is attached only when it has a clear lead.
Ambiguous and weak matches remain ordinary editable world collision.

At preview and bake time, an attached component receives the current TIE transform
relative to its stored import transform. Forge removes the unchanged source component,
applies that delta to its original typed faces, and inserts the result before native
octant validation and writing. Translation, rotation, scale, mirroring, TIE disable,
and TIE deletion therefore affect the recovered component without regenerating it
from render geometry.

Translation is bidirectional: translating an attached collision component translates
its TIE instead, so every component attached to that instance stays together.

Older projects migrate with no attachments. The source-backed base-entity migration
may recover them when the verified original ISO and catalog assets are available.
