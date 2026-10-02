# M2 collision editor qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Status: automated gate passed; manual PCSX2 gate partially passed

## Automated gate

The retained [all-level collision report](uya-collision-roundtrip.json) was generated
from the configured verified clean ISO on 2026-10-01 UTC. It contains no game bytes
or local filesystem paths.

| Metric | Result |
| --- | ---: |
| Populated levels | 51 / 51 passed |
| Primary and chunk collision payloads | 62 / 62 byte-identical |
| Collision source data | 90.69 MiB |
| Solid pieces | 99,257 |
| Player barriers | 1,495 |
| Faces | 2,356,922 |
| SHA-256 mismatches | 0 |
| Diagnostics | 0 |

Run the audit again with:

```bash
dotnet run --project tests/RatchetPs2.LevelTests/RatchetPs2.LevelTests.csproj \
  --configuration Release -- \
  --qualify-uya-collision <clean-ntsc-u-iso> <report.json>
```

`npm test` additionally verifies translation and deletion of solid collision and
player barriers through project save, collision-only bake invalidation, level-WAD
pack, and native semantic re-read. Collision-specific cancellation and injected
staged-output corruption preserve the last known-good staging manifest. The shared
build/patch tests cover cancellation, recovery, and development-ISO preservation.

## Manual PCSX2 gate

Recorded run: 2026-09-30, project owner. Moving several solid-collision pieces and
deleting solid collision worked in-game; unrelated collision remained at its source
positions. The level and PCSX2 version were not recorded.

Use the packaged-app procedure in the
[M4 external PCSX2 loop qualification](M4-pcsx2-test-loop.md). Build and externally
reload the development ISO after each row; do not use a savestate after a full-image
replacement.

| Scenario | Edit | Expected result | Status |
| --- | --- | --- | --- |
| Move solid | Translate an obvious floor or wall piece. | Player/world collision moves to the displayed effective transform. | Passed. |
| Delete solid | Delete an isolated solid piece. | The deleted surface no longer blocks gameplay while unrelated collision remains unchanged. | Passed. |
| Move barrier | Translate an obvious player barrier. | The player is blocked at the new position, not the old one. | Pending. |
| Delete barrier | Delete that barrier. | The player can cross its former boundary. | Pending. |
| Sound types | Cross two surfaces with different high-nibble sound IDs. | Their distinct in-game surface sounds remain intact after repack. | Pending. |

Record the level, Forge build, PCSX2 version, effective transforms, result, and local
log reference here after the live run. Do not retain proprietary game bytes.
