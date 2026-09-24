# M4 level-WAD pack qualification

Date: 2026-09-22  
Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Command: `npm test`

The fixture refuses incomplete staging, packs a validated ten-layer snapshot through
the in-process SDK, reopens the archive inventory, checks all replacements and opaque
hashes, retains golden output SHA-256
`7a5e956d3b0ec11ba9829623f813f590771baeb830aefef80c8d558acf5a46d6`,
and verifies an edited tie translation after SDK rebuild. Cancellation during SDK
inventory publishes no output.

Result: passed.
