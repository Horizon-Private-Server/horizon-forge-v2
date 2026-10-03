# M2 instanced TIE collision qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Status: automated gate passed; manual PCSX2 gate pending

## Automated gate

Run `npm test` and the focused SDK collision checks:

```bash
dotnet run --project ../ratchet-ps2-cli/tests/RatchetPs2.LevelTests/RatchetPs2.LevelTests.csproj \
  --configuration Release -- --uya-collision
```

The retained automated gate verifies:

- deterministic Surface and smooth sectioned Solid hull generation;
- native quantization, rotation, scale, mirrored winding, and octant-boundary ownership;
- packed-output re-read of transformed/mirrored instances plus entity disable and deletion;
- complete primary-collision analysis across source faces and enabled proxy instances;
- exact parent TIE and proxy-blob verification after preflight;
- collision-only invalidation for bindings, transforms, removal, and per-instance opt-out;
- rejection of combined octant pressure from individually valid overlapping proxies;
- last-known-good staging preservation after cancellation, unsafe composition, missing or
  corrupt assets, and semantic output failure; and
- byte-identical repeated bake/pack output for equal inputs and source identity after
  removing the final binding.

The focused generation gate verifies that increasing profile sections retains a
vertical inset, caps every radial contour at twelve vertices, and remains bounded by
the requested section count. Combined scene analysis remains authoritative. Retained
corpus evidence can be regenerated once a representative source corpus is selected.

## Manual PCSX2 gate

Use the packaged-app procedure from the
[M4 external PCSX2 loop qualification](M4-pcsx2-test-loop.md). Use a level and TIE
whose collision can be reached repeatedly without a savestate after full-image
replacement. Build and externally reload the development ISO after each row.

| Scenario | Edit | Expected result | Status |
| --- | --- | --- | --- |
| Source baseline | Apply one proxy near unchanged native floor and wall collision. | The proxy blocks the player and nearby native collision remains unchanged. | Pending. |
| Rotation and scale | Place visible copies with obvious rotation and supported non-uniform scale. | Blocking follows each rendered copy's transformed outer surface, with no collision left at an untransformed pose. | Pending. |
| Mirrored copy | Mirror one reachable copy on a single axis. | The mirrored surface blocks from the correct side without missing back-facing triangles. | Pending. |
| Retained opening | Use Decimated mesh on a fence, rail, or hollow asset with an authored opening. | The chosen opening remains traversable while the surrounding proxy blocks. | Pending. |
| Per-instance opt-out | Disable collision on one of two matching visible copies. | The opted-out copy remains visible but does not block; the other copy still blocks. | Pending. |
| Remove binding | Remove the exact-asset binding and rebuild. | All matching generated collision disappears and native source collision remains unaffected. | Pending. |
| Repeated build | Build and reload again without changing the project. | Forge reports no baked layers and gameplay behavior is unchanged. | Pending. |

## Runtime-pressure evidence

The UI must continue to label soft pressure as unqualified until retained live evidence
supports a versioned profile. For one low, medium, and highest-valid combined placement,
record the following without retaining proprietary bytes:

| Field | Recorded value |
| --- | --- |
| Date, tester, platform | Pending |
| Forge version/commit and SDK version | Pending |
| PCSX2 version and renderer | Pending |
| Level and TIE Asset ID/catalog aliases | Pending |
| Recipe/generator version and settings | Pending |
| Instance positions, rotations, scales, and opt-outs | Pending |
| Combined faces and occupied octants | Pending |
| Worst octant coordinate, faces, vertices, quads, and encoded bytes | Pending |
| Hard violations and displayed pressure label | Pending |
| Build/patch result and local log reference | Pending |
| In-game blocking result | Pending |

Do not infer a soft threshold from native field widths, standalone candidates, or one
successful placement. Retain the maximum stable observation and any lower failing
observation; introduce a versioned runtime-safe profile only when the evidence gives a
conservative boundary. Do not commit ISOs, extracted game data, screenshots containing
local paths, or proprietary model/collision bytes.

M2-040 is complete when every manual scenario has a tester, date, packaged Forge
version, PCSX2 version, result, and retained local log reference, and the pressure
record either backs a versioned soft profile or explicitly remains unqualified.
