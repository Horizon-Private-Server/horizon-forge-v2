# M15 sound import and playback qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Date: 2026-10-20  
Status: automated and packaged-Linux startup gates passed; packaged sound walkthrough,
Windows parity, and PCSX2 playback evidence pending

## Automated gate

Run `npm test` in Forge and the SDK sound contract with:

```text
dotnet run --project tests/RatchetPs2.LevelTests/RatchetPs2.LevelTests.csproj \
  --configuration Release -p:BuildInParallel=false -m:1 -- --uya-sound-contract
```

The current gate verifies:

- deterministic inventory and preview behavior over all ten local UYA banks and 50
  Deadlocked banks, including pitch, random/cycle selection, short clips, looping,
  child sounds, control flow, and the recovered production opcode set;
- byte-exact no-edit output plus bounded append, shared-sample reuse, same-bank child
  relocation, remap relocation, alignment, capacity rejection, cancellation, and
  semantic re-read;
- a corpus-backed level 41 to level 03 direct-sound import whose canonical program
  identity survives composition and whose target Moby remap points at the final
  appended slot;
- preservation of source grain/sample opaque bytes and unrelated remap records;
- missing, wrong-kind, recursive, duplicate, corrupt, and over-capacity dependency
  rejection before replacement; and
- recovery, cleanup, bake fingerprints, and ZIP transfer of imported encoded samples
  without relying on the donor path.

The Forge bridge/domain suite passed with all ten UYA bank/remap pairs projected. The
full TypeScript, renderer, bridge, host, update, notification, performance, and SDK
sound suites also passed. The update-download test required normal loopback access and
was rerun successfully outside the restricted test sandbox.

## Packaged Linux evidence

The local x64 package was rebuilt from the working tree based at commit
`4a81e188ed9776450be945aa910276ae9a4bde4c`. Its package manifest reports Forge
2.0.0 development, SDK v0.8.1, 418 files, and the local build workflow. The executable
SHA-256 is:

```text
09f76150ca151b0d8eec9d984ef290393ae73583bf6508655799a61a351086d2
```

`scripts/smoke-package-linux.sh` passed against a fresh isolated settings directory;
the retained local log is `artifacts/ci-logs/m15-sound-import-linux-smoke.log`. The log
confirms that the packaged self-contained `Forge.Host` starts with the packaged SDK.
Generated packages, logs, projects, retail data, and patched disc images remain local
and are not committed.

## Remaining packaged and runtime matrix

Use one direct program from level 41 that is absent from level 03. For the Moby case,
select a qualified level 41 Moby sound slot whose complete program/sample graph is
shown in Forge. Record the Forge package manifest, source/target levels, Sound Asset
IDs, target Moby class and slot, final native slot, output hashes, PCSX2 version, and
the `build-patch.log` path. Do not record retail bytes or local source paths.

| Environment | Scenario | Expected result | Status |
| --- | --- | --- | --- |
| Linux packaged | Open the target and donor projects, preview the selected direct sound repeatedly, then remove access to the donor and reopen the target | Inventory/identity remain stable; playback starts every time; authored variation is not pinned to one branch; no donor path is needed. | Pending manual walkthrough |
| Linux packaged build | Import the direct sound and Moby sound closure, dry-run, build twice from clean staging, and compare bank/header/output hashes | The plan reports the same appended/reused samples and final slots; both builds are deterministic; the Moby slot resolves to the planned sound. | Automated composition passed; packaged walkthrough pending |
| Linux + PCSX2 | Patch the development image, trigger the direct and Moby sounds repeatedly, and capture representative output | Both sounds play at the expected pitch/rate; short clips start reliably; loops have no new pop; random/cycle pitch behavior varies as authored. | Pending; no callable PCSX2 binary in this environment |
| Linux interruption/corruption | Cancel during composition, then retry with a missing/corrupt attached sample | Cancellation and rejection leave the prior stage/image usable and identify the exact dependency edge. | Automated gate passed; packaged walkthrough pending |
| Windows packaged nightly | Transfer the ZIP with donor data unavailable, reopen, preview, clean-build, and compare plan/output hashes with Linux | Asset IDs, preview metadata/PCM behavior, final slots, and output hashes agree with Linux. | Pending Windows runner |

M15-006 remains open until the Linux packaged sound workflow, PCSX2 playback, and
Windows parity rows retain evidence. The automated Moby-remap fixture proves the writer
boundary; M16-007 will expose the ordinary one-action Moby dependency-closure import
workflow used for its final packaged walkthrough. M16-007 may proceed on M15-005;
M15-006 remains a gate for M16-009 and the P3 release rather than circularly blocking
the workflow needed to finish its own qualification.

## Disclosed preview and composition limits

- Named cross-bank child/branch targets and external plug-in messages are preserved
  but are not previewed or imported as if their dependency closure were known.
- Unknown operations and unqualified optional names/user-data regions are preserved;
  any change requiring Forge to rewrite them is blocked.
- Forge preview models the recovered grain scheduler, pitch, control flow, and ADPCM
  loops, but does not claim hardware-exact SPU ADSR or reverb.
- The direct preview preference affects bank clips. Ambient volume is reserved for the
  later ambient simulation path and does not imply streamed/ambient runtime support.
- Composition is bounded by native signed counts, 24-bit grain-data offsets, the PS2
  SPU allocation, the 16 MiB metadata limit, and the 64 MiB bank limit.
