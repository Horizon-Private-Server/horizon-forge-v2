# Forge project format v6

Imported solid-collision components retain source-derived links to the TIE and shrub
instances they overlap. Links are many-to-many and do not add metadata to the game
collision payload or replace the authored collision geometry.

During source import, Forge compares each solid component against instance geometry in
local space and ranks qualifying overlaps by containment, coverage, and center alignment.
Small fully-contained fragments are retained when no normal TIE match claims the piece.
The resulting link graph supports grouping, navigation, and movement without assigning
shared collision to an arbitrary owner.

Rigid transforms are bidirectional across each connected link component. Moving or
rotating one linked TIE, shrub, or collision piece transforms every connected member
exactly once. Collision position and rotation are stored directly on the collision
entity and baked through the normal native collision writer; source collision scaling
remains unsupported.

On editor open, Forge recovers missing links from the retained verified collision and
instance assets; saving the project persists the result. Any obsolete singular attachment
property is discarded while loading and is never used as a fallback.
