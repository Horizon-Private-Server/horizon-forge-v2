# Horizon Forge bridge protocol v3

Version three retains the framing, byte order, limits, message kinds, opcodes, and
error codes from [protocol v2](bridge-protocol-v2.md). The header version field is
`3`; older peers are rejected during startup before decoding incompatible payloads.

The editor command payload adds typed reference updates, palette-optimization
profiles, and bounded HUD image edits. Editor snapshots add the palette profile,
HUD inventory and dirty state, and typed project references. Asset-preview requests
also carry the open project path so project-attached assets can be resolved.

The language-neutral framing vectors in
[`tests/fixtures/bridge-v3.json`](../tests/fixtures/bridge-v3.json) cover the v3
header. All variable-length fields retain the protocol's existing bounds and strict
trailing-data rejection.
