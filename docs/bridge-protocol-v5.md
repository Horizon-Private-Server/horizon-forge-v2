# Forge binary bridge protocol v5

Protocol v5 appends optional directional-light colors and directions to each editor
entity snapshot. The renderer uses the records, their source slots, and entity
rotations to reconstruct tfrag vertex lighting instead of displaying the game's
uninitialized cached colors.

The Electron client and Forge.Host must use the same protocol version. A mismatch
is rejected during handshake. Framing and request lifecycle are unchanged.

The interoperability vectors in
[`tests/fixtures/bridge-v5.json`](../tests/fixtures/bridge-v5.json) cover v5 framing.
Earlier documents and fixtures remain historical compatibility records.
