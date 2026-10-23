# UYA 989snd bank and remap contract v1

Status: M15-001 frozen; production-grain preview coverage updated (2026-10-19)

This contract defines the UYA NTSC-U 1.00 `sound.bnk` and asset-header sound-remap
boundary used by M15. Writable behavior remains limited to the UYA corpus, while the
preview interpreter also implements recovered same-bank runtime operations whose
parameter rules are complete. Unrecognized or external plug-in behavior remains
attributable and preserved instead of being approximated.

## Evidence and provenance

The primary evidence is ten ignored, locally extracted retail banks and their matching
asset headers. Retail bytes are not committed. The SDK contract test contains a
synthetic fixture and checks any locally available corpus files against the structural
measurements below.

The local [`dec989snd`](https://github.com/Ziemas/dec989snd) checkout at commit
`c5f5dfbf098fd2237da96575eb8fb644d937f0b1` was reviewed only as behavioral and
layout evidence for the 989snd loader and playback system. It identifies the examined
runtime as `989SND.IRX` and records release input SHA-1
`ca00b1c1556fa615ed6b84513b929d6903d00493`. The repository has no root license or
notice governing its decompiled source. Forge and RatchetPs2 therefore do not copy,
compile, link, translate, or redistribute that source. Implementations are independent
C# derived from observed UYA bytes, documented behavior, and synthetic tests. Reuse
terms must be established before that boundary changes.

The recovered instruction behavior was cross-checked against the byte-matching
Deadlocked EE client at ratchet-project commit
`c8bbae2e8a125a76e5834159cc78b8f7bd836678` and the retail Ghidra program. Those EE
sources confirm the six-word bank/index/volume/pan/pitch-modifier/pitch-bend command
boundary; the IOP grain state machine remains the authority for playback behavior.

## File container

All observed UYA banks use a little-endian type-3 chunk container:

| Offset | Type | Meaning |
| --- | --- | --- |
| `0x00` | `u32` | container type; UYA v1 requires `3` |
| `0x04` | `u32` | chunk count; UYA v1 requires `2` |
| `0x08` | `u32,u32` | metadata chunk offset and byte length |
| `0x10` | `u32,u32` | encoded-sample chunk offset and byte length |

The 24-byte table, metadata chunk, and sample chunk are non-overlapping and bounded by
the file. In the corpus the metadata starts at `0x18`, the sample chunk immediately
follows it, the sample chunk is a multiple of one 16-byte PS2 ADPCM frame, and the file
ends with zero sector padding to a 2048-byte boundary. Padding belongs to the container
for byte identity but not to any sample identity. A changed writer may recompute chunk
offsets and padding; it may not preserve an invalid layout merely to retain offsets.

## `SBlk` v3 metadata

The metadata chunk begins with this 64-byte block header:

| Offset | Type | Meaning |
| --- | --- | --- |
| `0x00` | `u32` | magic `0x6B6C4253` (`SBlk`) |
| `0x04` | `u32` | version; observed and required value `3` |
| `0x08` | `u32` | flags; every observed bank uses `0x00000004` |
| `0x0C` | `u32` | block ID; every observed bank uses `0x00574144` (`DAW`) |
| `0x10` | `u8` + 3 bytes | block number and reserved bytes |
| `0x14` | `s16` | reserved |
| `0x16` | `s16` | playable sound count |
| `0x18` | `s16` | grain count |
| `0x1A` | `s16` | declared VAG/sample count |
| `0x1C` | `u32` | relative offset of 12-byte sound records |
| `0x20` | `u32` | relative offset of 8-byte grain records |
| `0x24` | `u32` | runtime SPU address field; never a file pointer |
| `0x28` | `u32` | encoded sample bytes transferred to SPU RAM |
| `0x2C` | `u32` | requested SPU allocation size |
| `0x30` | `u32` | runtime next-block pointer; zero on disk |
| `0x34` | `u32` | relative offset of grain parameter data |
| `0x38` | `u32` | optional names region offset |
| `0x3C` | `u32` | optional per-sound user-data offset |

The corpus has sound records at `0x40`, grains immediately after the sound array, and
grain parameter data immediately after the grain array. Counts are non-negative native
signed 16-bit values. Relative offsets are metadata-chunk-relative, four-byte aligned,
and must bound their complete table before allocation. Runtime pointer fields are
relocations, not stable identity.

Each 12-byte playable sound record stores signed volume, volume group, signed pan,
signed grain count, instance limit, 16-bit flags, and a relative offset into the global
grain array. Each grain is a 32-bit opcode followed by a signed 32-bit delay. The opcode
packs three signed argument bytes and an unsigned type byte; types with external
parameters instead use the low 24 bits as a grain-data-relative offset.

The reviewed runtime defines types `0x00` through `0x09` and `0x14` through `0x2C`.
The corpus observes tone (`0x01`), LFO (`0x04`), and control types `0x14` through
`0x24` plus `0x27` through `0x2B`. It does not demonstrate cross-bank references,
child-sound operations, plugins, branches, tone2, random-marker jumps, wait-for-voices,
or copy-register. The preview interpreter supports the recovered same-bank forms of
those instructions, but named cross-bank child/branch targets and external plug-in
messages remain unsupported. Unknown type bytes are preserved and never executed by
Forge.

Tone parameters are 24 bytes: priority, volume, center note/fine, pan, note and pitch-
bend ranges, ADSR1/ADSR2, flags, a sample relocation, and a reserved word. The sample
relocation is an offset in the encoded-sample chunk. LFO parameters are 16 bytes.
Plugin and child/branch parameter records are 32 bytes where encountered. Optional
names (`flags & 0x100`) and user data (`flags & 0x200`) have reviewed layouts, but both
flags and offsets are zero in every UYA corpus bank, so M15 treats those regions as
unqualified and opaque.

## Samples, looping, and rate

Samples are sequences of 16-byte PS2 SPU ADPCM frames addressed relative to the sample
chunk. A sample ends at the first frame whose flag byte has bit `0x01`; loop-repeat and
loop-start are represented by flag bits `0x02` and `0x04`. Every observed tone resolves
within the sample chunk, every observed sample terminates, sample ranges do not overlap,
and the number of unique tone sample offsets equals the declared VAG count.

`SBlk` does not store a VAG-file sample-rate header. Playback pitch comes from the tone
center note/fine, the caller note/fine, pitch bend/modifier, and the PS2 voice pitch
conversion. Inventory exposes those source fields and ADPCM loop markers rather than
inventing a sample rate. M15-002 may label an effective preview rate only for a fully
specified supported playback path.

### Inventory and preview boundary

The M15-002 SDK reader inventories every program, operation, typed parameter range,
sample dependency, source range, loop boundary, supported state, and qualified
canonical identity. It accepts the frozen UYA layout only and rejects invalid chunk,
table, relocation, dependency-graph, ADPCM, and allocation arithmetic before decoding.

Preview executes the recovered 240 Hz `SBlk` grain state machine with bounded
instruction and PCM budgets. Production coverage includes layered/delayed tones,
random and cycle selection, random delay/pitch, local/global register operations and
tests, markers and jumps, program loops, voice key-off/kill, stop markers, and volume,
pan, pitch-modifier, and pitch-bend LFO targets. Tone volume/pan register indirection,
sample loops, pitch changes, and noise/muted flags are resolved rather than rejected.
Same-bank child/branch forms and the recovered tone2 handler are also implemented.

One 16-byte PS2 ADPCM frame decodes to 28 signed mono PCM16 frames using predictors
0 through 4 and shifts 0 through 12. A single static tone retains the direct mono path
and returns:

- an effective sample rate of
  `baseRate * 2^((60*128 - abs(centerNote)*128 + centerFine) / (12*128))`, rounded
  to the nearest integer, where a negative center note selects the PS2 48,000 Hz base
  and a non-negative center note selects the compatibility 44,100 Hz base. The
  center-fine sign follows the shipped `sceSdNote2Pitch` implementation;
- the normalized 989snd pan angle `(programPan + tonePan) mod 360` and static gain
  `(programVolume / 127) * (toneVolume / 127)` as separate mixer metadata; and
- loop start/end PCM-frame indexes derived from ADPCM flags `0x04` and `0x01|0x02`.

All other programs render to bounded interleaved stereo PCM16 at 48,000 Hz. The mixer
applies grain timing, sample-rate conversion, per-tone/program gain and pan, live pitch
and supported LFO state. Random choices use a fresh preview stream, RAND_PLAY retains
its previous branch during execution, and variable previews bypass cache reuse so
successive playback can exercise their authored variation. Hardware-exact SPU ADSR
and reverb are not baked into desktop PCM.

Callers supply a decode-frame budget capped at 8,000,000 frames. Inventory and decode
accept cancellation and check it at bounded table/frame intervals. M15-004 owns mixer
gain/pan/envelope application and playback lifecycle.

Unreferenced bytes before/between sample ranges and unused grain-data bytes are opaque
container content. They survive no-edit output and are not silently assigned to a
nearby sample.

## Sound remap

The UYA asset header points to a little-endian sound-remap block. Its first eight bytes
are four non-negative `s16` values:

1. offset of fixed 32-byte "other" records;
2. other-record count;
3. offset of the global sound-ID array; and
4. global sound-ID count.

Bytes `0x08..otherOffset` are one four-byte record per moby definition: an `s16` offset
and `s16` count selecting an ordered list of 32-bit sound indexes. A positive count
requires a nonzero bounded offset. A zero count ignores its offset; the corpus contains
unused nonzero values there, so writers preserve them rather than interpreting them.
Corpus moby slots contain either `-1` or an index in the matching bank; `-1` is the
only proven nullable sentinel. Global IDs are also 32-bit bank indexes. The 32-byte
other records are preserved as opaque until their owner and fields are proven.
SDK composition rebuilds the ordered moby lists against final program slots, preserves
opaque other records and global-list semantics, appends the rebuilt block to the asset
header, patches the owning offset, and semantically re-reads the result.

Typed project links use `(owner class, remap slot) -> Sound Asset ID`. Native sound
indexes remain provenance/output allocation only. A missing, wrong-bank, or non-`-1`
out-of-range value is a blocker with the owner and slot; it is never treated as null or
matched by number in the target bank.

## Canonical identities

All hashes use SHA-256 over a versioned, length-prefixed little-endian encoding.

`SoundSampleId` includes:

- domain `uya-989snd-sample-v1` and codec `ps2-spu-adpcm`;
- every 16-byte encoded frame from the referenced start through the first end frame,
  including predictor, shift, loop, and end flags.

It excludes chunk/file offsets, padding, and runtime SPU addresses. Consequently two
tones may share one sample identity while retaining different pitch, envelope, pan, or
program behavior.

Direct `SoundAssetId` values retain the `uya-989snd-program-v1` encoding. Compound
programs use `uya-989snd-program-v3`, which includes:

- domain and bank format version;
- sound volume, volume group, pan, instance limit, and all source flags;
- ordered grains with type, signed arguments or typed parameter payload, and delay;
- for each tone: priority, volume, center note/fine, pan, note/pitch-bend ranges,
  ADSR1/ADSR2, flags, reserved value, and referenced `SoundSampleId` instead of its
  relocation;
- every qualified LFO/control parameter;
- qualified child/cross-sound edges as target `SoundAssetId`s; and
- associated qualified name/user data when present.

Raw relocations, native sound indexes, block number/ID, and container padding are
excluded. Unsupported/opaque grain parameters receive a reproducible source fingerprint
for diagnostics and preservation but not an importable `SoundAssetId`; this avoids
claiming dependency closure that the adapter cannot prove. Recursive qualified sound
graphs are rejected in v1 rather than assigned order-dependent IDs.

## Preservation and changed-output identity

- With no sound or remap edits, composition returns the original complete bank and
  remap byte spans. SHA-256 equality is the byte-identity gate.
- An inventory re-read of no-edit output must match ordered playable fields, grain
  programs, parameter payloads, sample identities/ranges, loop markers, and remap
  owner/slot targets.
- Changed output is allowed to relocate/repack tables, grain data, samples, and sector
  padding. It must re-read to the planned ordered `SoundAssetId`/`SoundSampleId` graph,
  tone metadata, remap links, and opaque retained regions.
- Every untouched entry/sample preserves its encoded bytes. Unsupported or ambiguous
  content blocks a composition that would require relocating or rewriting it.

## Bounds

The v1 boundary rejects before allocation when:

- a bank exceeds 64 MiB, metadata exceeds 16 MiB, or encoded samples exceed 32 MiB;
- chunk arithmetic overflows, overlaps, exceeds the file, or violates required frame/
  table alignment;
- signed counts are negative, a sound has more than 127 grains, or any table window is
  outside metadata;
- a typed grain parameter, sample relocation, remap record, list, or ID is out of
  bounds;
- a sample lacks an end frame, sample ranges overlap, or decode expansion exceeds the
  M15-002 preview budget; or
- a dependency graph is cyclic, too deep, too large, unsupported, or ambiguous.

Native fields impose `s16` bank counts, `s8` grains per sound, 24-bit grain-data
offsets, `s16` remap offsets/counts, 16-byte ADPCM frames, and 2048-byte bank-file
alignment. Writers must also preflight PS2 SPU allocation and final container size.

## Corpus report

All files have two chunks, `SBlk` v3/flags `0x4`, valid terminating samples, zero bad
table windows, and moby-remap entries limited to `-1` or a valid native sound index.
The hash covers the complete sector-padded bank.

| Level | SHA-256 | Header / sample bytes | Sounds / grains / samples | Moby configs / other / global IDs |
| --- | --- | --- | --- | --- |
| 01 | `d50bf983cc038241d751125e602d16a53288e9b17ac7cfb88157a23ec9bdf82e` | 27,740 / 1,583,152 | 321 / 1,107 / 261 | 227 / 42 / 256 |
| 03 | `75e5eea7a97dfa448cd073af57faa0a65d62ec1cc10a3d9508f6a7f209365ade` | 26,980 / 1,549,408 | 311 / 1,078 / 284 | 275 / 42 / 232 |
| 04 | `fc91a26e17303ce80176c4e88c8aec114cd9f0d442ee8e14050f0a348737453d` | 27,224 / 1,683,568 | 322 / 1,042 / 276 | 254 / 45 / 280 |
| 08 | `d085f2f371e375184b4edf2f2e71172f27a0ae821106743048d1007b40ad81b5` | 22,232 / 1,622,816 | 284 / 807 / 242 | 225 / 44 / 248 |
| 14 | `0e629977664acd4161b627358b91d4de4bb5c97fd4f68003f6db7306a83ece13` | 23,948 / 1,566,496 | 305 / 896 / 256 | 213 / 46 / 288 |
| 17 | `473d9edebc8f6f09d14540dff86ec8d3ee063991540d4117f92dfdd2786b458c` | 20,768 / 1,364,384 | 254 / 789 / 230 | 183 / 41 / 224 |
| 21 | `458f960271898412413ef424ee4818e0ad00fcce2dd4c1c133a52dac17735e3d` | 17,548 / 1,290,384 | 229 / 650 / 211 | 190 / 46 / 288 |
| 28 | `2ec3752bdfc8c4a3ac67568a2445446825f7132495acb31abae224f6e7a0cda1` | 17,144 / 1,324,240 | 242 / 618 / 199 | 175 / 40 / 216 |
| 41 | `c878f9b9a7b511bee88e373a4c9721af41a38c04d5277334988717b67be8fd27` | 19,512 / 1,780,656 | 280 / 706 / 268 | 160 / 46 / 288 |
| 48 | `91913d26c5afcec811f53de3765d181f131ec430860184b6c596a32bbac7a912` | 15,156 / 1,434,752 | 225 / 530 / 217 | 112 / 46 / 200 |

Levels 01 and 48 are the minimum structurally distinct identity fixtures: they differ
in counts, table sizes, padding, opcode sets, looped sample count, and remap shape. The
full ten-level report prevents either endpoint from becoming an accidental special
case.
