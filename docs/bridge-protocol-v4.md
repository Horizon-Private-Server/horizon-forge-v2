# Forge binary bridge protocol v4

Protocol v4 extends the v3 editor payload with FX texture project state and four
authoritative commands: replace, restore source, append, and remove the last appended
texture. The framing, request lifecycle, cancellation behavior, and all other payloads
remain unchanged.

The Electron client and Forge.Host must use the same protocol version. A mismatch is
rejected during handshake rather than attempting to decode an incompatible editor
payload.

The checked-in interoperability vectors in
[`tests/fixtures/bridge-v4.json`](../tests/fixtures/bridge-v4.json) cover v4 framing.
The v3 document and fixture remain as historical compatibility records.
