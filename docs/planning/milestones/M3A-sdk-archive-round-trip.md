# M3A — SDK archive round-trip

Priority: P0  
Depends on: M3-003  
Task register: [M3A tasks](../tasks/M3A-tasks.md)

## Outcome

The pinned Ratchet SDK can decompose an NTSC-U UYA level WAD into complete,
byte-owned in-memory regions and rebuild its uncompressed representation exactly
before producing a validated compressed WAD. Forge consumes this API directly;
it does not implement archive layouts or invoke a CLI subprocess.

## Deliverables

- Complete UYA level-container inventory, including nested payloads, padding,
  gaps, and unknown regions.
- In-memory, game-specific uncompressed writers with checked deterministic layout.
- Deterministic compression plus decompression verification.
- One host-independent SDK operation returning output, hashes, diagnostics,
  progress, and cancellation state.
- Relocatable in-memory composition for editable sky, tfrag, collision, and
  chunk-tfrag payloads.
- Full NTSC-U UYA level-corpus round-trip and performance evidence.

## Exit gate

Every readable level in a locally supplied clean NTSC-U UYA ISO completes an
in-memory unpack/repack whose pre-compression bytes and SHA-256 match each
uncompressed source container or decompressed compressed-source container. New
compressed results decompress to those same bytes, no intermediate loose files
are created, malformed layouts fail before output is accepted, and the largest-level
allocation/throughput report shows no per-piece full-buffer copies or repeated
whole-buffer concatenation.
