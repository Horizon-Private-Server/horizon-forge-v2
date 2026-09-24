# M3A task register — SDK archive round-trip

Milestone: [M3A — SDK archive round-trip](../milestones/M3A-sdk-archive-round-trip.md)

All implementation in this register belongs in reusable ratchet-ps2-cli SDK,
Core, or UYA game assemblies. Forge.Host calls those APIs directly. CLI wiring is
optional diagnostic UX and must not own binary logic.

## M3A-001 — Inventory the complete UYA level-container graph

Requirements: FR-SDK-002, FR-BUILD-002, FR-BUILD-004, DR-018  
Depends on: M0-002, M3-003

Define the lossless in-memory representation for the primary level WAD and its
nested level-data, gameplay, asset, chunk, bank, sound, occlusion, HUD, transition,
and opaque regions.

Acceptance:

- Every byte at each container level is owned exactly once by a header field,
  known payload, alignment/padding span, or named opaque span.
- Entries retain container path, source offset, length, alignment, ordering, and
  compressed/uncompressed provenance; zero-length entries remain representable.
- Nested views use `ReadOnlyMemory<byte>` slices of their backing input where
  possible instead of cloning every payload.
- Recognized empty sentinels such as `(-1, 0)` remain represented; non-empty
  negative, overflowing, overlapping, out-of-bounds, or ambiguous ranges fail
  with the container path and offending range.

Verification: synthetic boundary/overlap fixtures and interval-coverage checks
for representative locally supplied UYA levels.

Implementation: `UyaLevelWadInventoryReader` in the UYA game assembly inventories
the outer WAD, level-data WAD, decoded gameplay core, and asset payload directly
from `ReadOnlyMemory<byte>`. Ordered slots retain declarations, aliases, alignment,
compression, hashes, and source-backed slices; regions classify headers, padding,
payloads, gaps, and trailing bytes with exact coverage. Empty `(-1, 0)` sentinels
remain visible without becoming invalid slices. Synthetic range fixtures and all 45
locally supplied level WADs pass the focused inventory check.

## M3A-002 — Implement byte-exact uncompressed UYA writers

Requirements: FR-SDK-002, FR-BUILD-002, FR-BUILD-004, NFR-REL-001  
Depends on: M3A-001

Build nested containers bottom-up in the UYA game assembly while preserving
untouched payload, unknown, padding, and reserved bytes.

Acceptance:

- An unchanged image rebuilds byte-for-byte against its source bytes when
  uncompressed or its decompressed source bytes when compressed, including headers,
  gaps, alignment, padding, reserved fields, and trailing bytes.
- Replaced payloads cause affected offsets, lengths, sector counts, and alignment
  to be recalculated with checked arithmetic and deterministic ordering.
- Newly introduced padding uses the format-defined deterministic fill, never stale
  pooled memory or bytes from an unrelated source span.
- The writer never mutates input memory and never exposes a successful result
  until all ranges and declared sizes validate.
- Reader re-entry returns the same supported values and exact opaque payload
  hashes.

Verification: uncompressed golden round trips, one-change layout fixtures, and
checked-overflow/failure injection tests.

Implementation: `UyaLevelWadWriter` rebuilds inventory containers into one
pre-sized destination, copying untouched headers, gaps, opaque spans, padding,
and payloads directly from their owned regions. Nested asset, gameplay, and
level-data changes promote bottom-up; affected byte offsets, gameplay pointers,
sector offsets, and lengths are rewritten with checked arithmetic. New alignment
bytes are zero-filled, conflicting parent/child or aliased replacements fail, and
input memory is never mutated; stale source memory is rejected by SHA-256.
Unchanged containers and complete level WADs are byte-identical across all 45
locally supplied fixtures; growing synthetic payloads also pass reader re-entry
and opaque-byte preservation checks.

## M3A-003 — Gate WAD compression with semantic verification

Requirements: FR-BUILD-003, FR-BAKE-003, NFR-REL-002, NFR-REL-003  
Depends on: M3A-002

Use the existing SDK compression implementation only after uncompressed output
passes validation, then verify the compressed result by decompressing it in memory.

Acceptance:

- Equal uncompressed input and compressor version produce deterministic compressed
  bytes; matching the retail compressor's byte choices is not required.
- `Decompress(Compress(repacked))` is byte-identical to the validated uncompressed
  repack and reports both hashes and sizes.
- Compressed headers, declared lengths, packet bounds, alignment, and termination
  are validated before the result is accepted.
- Configurable output/expansion bounds and cancellation prevent corrupt input from
  causing unbounded decompression or allocation.
- Empty, boundary-sized, incompressible, highly repetitive, truncated, and corrupt
  inputs have runnable fixtures and actionable failures.

Verification: compressor golden vectors, deterministic repeats, fuzz/property
round trips, and malformed packet fixtures.

Implementation: Core now exposes `CompressVerified`, which returns compressed
bytes, sizes, and SHA-256 values only after bounded decompression reproduces the
input exactly. The existing codec remains the single implementation; its match
probe now handles zero-, one-, two-, and boundary-length inputs safely.
Decompression reads spans without cloning, validates headers, declared stream
length, packet reads, lookbacks, alignment skips, and complete termination, and
enforces configurable output/expansion limits plus cancellation. Golden vectors,
deterministic repeats, seeded property cases, chunk boundaries, repetitive data,
truncation, corrupt packets, bounds, cancellation, all 45 local UYA WADs, and UYA
browser SDK generation pass.

## M3A-004 — Expose the verified in-memory SDK workflow

Requirements: FR-SDK-002, FR-BUILD-001 through FR-BUILD-004, NFR-PERF-004  
Depends on: M3A-002, M3A-003

Expose one host-independent SDK operation that reads a UYA level image, inventories
it, rebuilds it, validates pre-compression equality when requested, compresses it,
and returns structured results without filesystem-only orchestration.

Acceptance:

- Forge.Host references and calls the SDK operation in-process; no CLI subprocess,
  JSON transport, or duplicate archive writer is introduced.
- Byte/memory and seekable-stream entry points share the same implementation.
- Results identify SDK/schema versions, source/uncompressed/compressed hashes and
  sizes, changed regions, warnings, and blocking diagnostics.
- Progress is phase-aware and cancellation is honored before result publication;
  partial output is never presented as successful.

Verification: SDK contract tests using memory and stream inputs, cancellation at
each phase, and equality of both entry-point results.

Implementation: `UyaLevelArchiveBuilder` in `RatchetPs2.Sdk` is the single
host-independent composition path for memory and seekable-stream callers. It
inventories the source, produces and re-reads the uncompressed rebuild, optionally
requires source equality, recompresses WAD-backed payloads with semantic
verification, rebuilds parents bottom-up, and re-reads the final patchable outer
image before publishing it. Results include schema/SDK versions, stage hashes and
sizes, per-payload compression records, changed regions, warnings, and structured
blocking diagnostics; failures expose no output. Contract fixtures cover memory
and stream parity, edited compressed gameplay, diagnostics, ordered progress, and
cancellation at reading, inventory, rebuild, validation, compression, and final
publication. Forge.Host calls the SDK in-process through
`UyaArchiveBuildService`; no CLI process or duplicate archive logic is used.

## M3A-005 — Qualify data cleanliness, memory, and throughput

Requirements: FR-BUILD-002 through FR-BUILD-004, NFR-PERF-005, NFR-PERF-006,
NFR-REL-001 through NFR-REL-003  
Depends on: M3A-004

Run the verified workflow over every readable level in a locally supplied clean
NTSC-U UYA ISO and record correctness and performance without retaining game data.

Acceptance:

- Every level reports byte-identical source/repack hashes for uncompressed
  containers and decompressed-source/repack hashes for compressed containers,
  then byte-identical decompressed-result and repack hashes.
- The operation reads the source WAD once, writes only the requested final output,
  and creates no per-piece or temporary loose files.
- Peak live bulk data is bounded to the source, one uncompressed destination, one
  active compressed destination, and reusable scratch space; payload count does
  not add a full-container copy per payload.
- Archive assembly is linear in bytes plus entries and does not repeatedly
  concatenate growing whole buffers.
- The retained report records per-level size, elapsed time, throughput, managed
  allocation, peak working set, compression ratio, and failure diagnostics but no
  proprietary bytes.

Verification: all-level local qualification report plus a synthetic CI corpus
covering smallest, largest-shape, sparse, alignment-heavy, and malformed layouts.

Implementation: the opt-in `--qualify-uya-iso` LevelTests command opens the ISO
once, processes every populated level in memory, and retains only JSON metrics,
hashes, and diagnostics. The synthetic qualification corpus covers all five
required layout classes during normal tests. The clean NTSC-U baseline passed all
51 levels and 3,162 hash checks with no diagnostics; its report and same-machine
performance summary live in `docs/qualification/`. Assembly uses five bulk-buffer
destinations per level regardless of payload count. Qualification also removed
hot-path compressor object, slice-array, and packet-list allocations without
changing the compressed format.

## M3A-006 — Compose editable UYA asset and chunk payloads

Requirements: FR-SDK-002, FR-BUILD-002 through FR-BUILD-004, NFR-REL-001  
Depends on: M3A-004

Relocate changed sky, primary tfrag, collision, and chunk-tfrag payloads in memory
while preserving every untouched asset byte and updating all affected native pointers.

Acceptance:

- Replacements may grow or shrink; packing is not limited to original slot capacity.
- Every affected asset-header model and payload pointer is relocated deterministically.
- Chunk terrain replacement preserves trailing payloads and updates their offsets.
- Unchanged composition remains byte-identical and output is semantically re-read
  before publication.
- Composition is byte-oriented, cancellable, creates no loose intermediates, and is
  owned by the Ratchet SDK rather than Forge.Host.

Verification: resized synthetic relocation fixtures, Forge staged-sky pack/re-read,
and a local all-level audit covering every available primary payload and chunk.

Implementation: `UyaLevelAssetComposer` rebuilds decoded asset WAD sections with
0x10 alignment, patches direct asset offsets plus moby/tie/shrub model pointers,
and verifies every replacement through the existing readers. It also recompresses
and verifies chunk terrain while shifting later chunk offsets without touching their
bytes. Forge routes changed staged base payloads through this API and validates the
packed archive against staging. Synthetic checks cover pointer relocation, untouched
payload retention, chunk suffix retention, and no-op equality; the clean local NTSC-U
corpus passed 51 levels, 94 resized primary asset compositions, and 11 chunk
compositions without retaining proprietary output.
