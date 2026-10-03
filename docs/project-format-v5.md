# Forge project format v5

Version five extends the compressed v4 project content with reusable TIE collision
proxy bindings. It does not place generated mesh geometry in project JSON.

`tieCollisionBindings` is an ordered list keyed by exact source TIE Asset ID. Each
entry stores the generated proxy Asset ID and a typed, versioned generation recipe,
selected LOD, raw collision type, and Hull profile-section count. Legacy Wrap fields
remain readable so projects created during early feature development are not corrupted.
A missing/zero profile count on an older Hull recipe means the legacy one-section hull.

The proxy is an immutable project-attached `Collision` asset stored through the
existing content-addressed blob layout. Its immediate parent is the exact source TIE
Asset ID. Binding references protect the blob from collection; replacement or removal
makes an otherwise unreferenced prior blob eligible for later explicit cleanup.
Cleanup refuses dirty projects and protects assets referenced by retained recovery
snapshots and attached-parent chains.

An entity may store `tieCollisionEnabled: false` to opt out. The default is omitted
and enabled, so current and future entities referencing the bound TIE inherit the
proxy without duplicating geometry or binding data per instance.

Version-zero through version-four documents migrate in memory. Older content receives
an empty binding list, and files are replaced only by the existing explicit,
journaled save path.

Because proxy paths are derived only from Asset IDs, moving or ZIP-transferring the
whole project preserves them without path rewriting. Repairing a missing source TIE
in another catalog restores the same content-addressed ID and does not alter bindings.
