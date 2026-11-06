# M16 cross-map import qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Date: 2026-10-21  
Status: ✅ Complete — maintainer sign-off (2026-11-06)

Inventory checkpoint before maintainer sign-off (2026-11-06): the UYA and converted-
donor matrices below record the earlier qualification checklist. The subsequent
maintainer sign-off closes M16-009, including the delivered donor profiles.

## Maintainer sign-off

The maintainer marked M16-009 complete on 2026-11-06, including the delivered
RC1/GC/DL donor scope.
This closes the task acceptance gates by maintainer sign-off. Earlier pending
rows and open-gate statements below are retained as historical checkpoints,
not current task status. No additional run metadata, measurements, hashes,
or capture references were supplied. Documented support limits still apply.
See the [planning sign-off record](../planning/README.md#maintainer-sign-off).

## Automated gate

Run `npm test` from the Forge repository. The current suite verifies:

- a synthetic level 40 to level 41 dry run with deterministic plan IDs, exact
  add/reuse/remap/remove/protect sets, conflict reporting, capacity rejection, and
  cancellation before mutation;
- atomic TIE/shrub, primary/streamed tfrag-owner, collision solid/player-barrier,
  cuboid/sphere/spline/grind-path, Moby dependency-closure, skybox, level-settings,
  directional-light, native map-group, portable whole-bank HUD, exact-count whole-bank FX,
  and complete donor occlusion application
  with native semantic re-read;
- Moby model, raw PVar, named and native-relocation internal/external reference,
  native-group, sound-program, sample, and remap closure without donor media;
- deterministic class/index allocation and repeated static, gameplay, terrain, sound,
  bake-manifest, packed-level, and one-click build output;
- dependency-safe batch removal, shared/protected survival, stale-preview rejection,
  undo/redo, recovery snapshots, and append-only provenance;
- cancellation and injected failure at every batch publication phase, cancelled bake
  manifest restoration, and cancelled build preservation of the development ISO; and
- save/reopen, directory move, recovery, and ZIP transfer with project-relative attached
  assets after the source catalog is unavailable.

The current host suites additionally exercise GC, RC1, and DL inventory/preview and
replacement, raw PVars/native links, undo/redo, and persistence. GC includes native
bake and target-scene retention during background/fog replacement; RC1/DL include
scene-setting/reference conversion. The focused SDK suite covers level-content
conversion, RC1 split TIE geometry/LODs, and model/sound conversion regressions.
`npm test` passed during the 2026-11-06 inventory.

## Converted-donor qualification

UYA NTSC-U 1.00 remains the target. All converted-donor selections use replacement;
unsupported model/dependency conversion blocks the affected family. Raw PVars and
native links are retained without translating gameplay behavior. Each profile needs
retained evidence independently of the original level 40 to level 41 UYA workflow.

| Donor profile | Cases to retain | Retail corpus report | Packaged Linux/Windows and PCSX2 |
| --- | --- | --- | --- |
| RC1 NTSC-U 1.00 | Native model conversion, split TIE counts/LODs, terrain/collision, sky, sounds, raw PVars, scene links and explicit mappings | Pending retained report | Pending retained evidence |
| GC NTSC-U 1.01 | Native conversion, terrain/collision, sounds/PVars, and background/fog replacement retaining the target scene | Pending retained report | Pending retained evidence |
| DL NTSC-U 1.00 | Resident core/world instances, streamed terrain/collision, sounds/PVars and scene links; mission-specific Moby instances are excluded | Pending retained report | Pending retained evidence |

Run the protocol test runner with `--qualify-rc1-content <iso> <catalog-root>`,
`--qualify-gc-content <iso> <catalog-root>`, or
`--qualify-dl-content <iso> <catalog-root>` to compare retail donor inventory with
converted availability in a temporary workspace. These checks do not qualify
packaged apply/build/patch or runtime behavior. Retain sanitized counts, diagnostics,
versions, and hashes locally; no retail payloads belong in the repository.

For each profile, use the hash manifest below with the actual donor identity/level,
then exercise preview/apply, undo/redo, save/reopen without donor media, cross-platform
transfer, repeated clean build/patch, PCSX2, and cancellation/recovery. Replace-mode
batch removal retains results because the ledger has no displaced-target snapshot;
undo/redo is the transactional reversal. Do not apply the UYA Add-mode removal
expectation to converted donors. Record scene links and unsupported conversion cases
explicitly. Streamed-terrain import does not imply streamed occlusion rebuild support.

The fixed adapter capability payload snapshot uses the UYA NTSC-U 1.00 request with
`ties=present` and every other inventory state unknown:

```text
schema: 1
sha256: e401b7e4a5ed0bedd0ae9fadfe1aeb4d29167e0b75f996c321cb9d71e720a35e
```

The protocol test fails if the ordered matrix, modes, readiness, ownership, dependency,
limit, or evidence text changes without an intentional snapshot update.
This is the current UYA-donor capability snapshot from
`UyaContentImportCapabilityTests`; converted donors restrict supported modes to
replacement and still require source inventory. It does not certify a retail run.

## Adapter capability snapshot

| Family | Mode | Ready | Default |
| --- | --- | --- | --- |
| TIEs | Add/Replace | Yes | Yes |
| Shrubs | Add/Replace | Yes | Yes |
| Tfrags | Add/Replace | Yes | Yes |
| Mobys | Add/Replace | Yes | No |
| Cuboids | Add/Replace | Yes | No |
| Spheres | Add/Replace | Yes | No |
| Splines | Add/Replace | Yes | No |
| Paths | Add/Replace | Yes | No |
| Areas | Add/Replace | Yes | No |
| Skybox | Replace | Yes | No |
| Level settings | Replace | Yes | No |
| Directional lights | Replace | Yes | No |
| Collision | Add/Replace | Yes | No |
| Map groups | Replace | Yes | No |
| HUD | Replace | Yes | No |
| FX | Replace | Yes | No |
| Occlusion | Replace | Yes | No |
| Opaque content | Unsupported | No | No |

## Development-Linux observations

The project owner reported the following development-build walkthroughs on 2026-10-20
and 2026-10-21. They are useful evidence but are provisional because the Forge revision,
PCSX2 version, hashes, capture, and build log were not retained:

- a level 41 Hoven project imported replacement static and terrain content from level 05
  Daxx, rebuilt successfully, and displayed the imported content in game;
- collision attached to replaced TIEs moved with the replacement rather than retaining
  the displaced TIE;
- donor skybox replacement rebuilt and displayed correctly in game;
- Moby `0x1F57` imported into level 41, its sounds played in game, and original target-map
  sounds remained intact; and
- level-settings replacement completed successfully in Forge.

This does not substitute level 40 for level 05, a packaged build, batch-removal runtime
evidence, or cross-platform transfer.

## Retained hash manifest

For each final Linux and Windows run, retain a text or JSON manifest beside the local
build log. Do not commit retail data, patched images, projects, captures, or machine-local
paths. In the authoritative import preview, choose **Copy record** after
completing any reference mappings. Forge copies a deterministic, path-free JSON record
containing a self-checking record SHA-256 plus the identities, hashes, selected-family
counts, and explicit reference choices below. The host-generated batch ID remains the
authoritative deterministic plan identity. Record package/SDK and source-catalog
versions separately; they are not all fields in the copied JSON. Record:

| Field | Linux | Windows |
| --- | --- | --- |
| Forge commit and package-manifest SHA-256 | Pending | Pending |
| UYA adapter snapshot SHA-256 | `e401b7e4a5ed0bedd0ae9fadfe1aeb4d29167e0b75f996c321cb9d71e720a35e` | Same required |
| Donor game, region/revision and source catalog revision | Pending | Same required |
| Source disc fingerprint | Pending | Same required |
| Donor level and level-WAD SHA-256 | Level 40 / pending | Same required |
| Target pre-import project fingerprint | Pending | Same required |
| Deterministic plan/batch ID | Pending | Same required |
| Target post-import project fingerprint | Pending | Same required |
| Portable project ZIP SHA-256 | Pending | Same archive required |
| First and repeated clean staging-manifest SHA-256 | Pending | Pending; must match Linux |
| First and repeated packed level-WAD SHA-256 | Pending | Pending; must match Linux |
| Development-image verification fingerprint | Pending | Pending |
| `build-patch.log` reference | Pending | Pending |

The deterministic batch ID binds the plan schema, target project/fingerprint, donor disc
and level hashes, donor level/profile, source catalog revision and inventory identities,
and sorted selections. The record SHA-256 detects changes
to the copied evidence, while the saved project fingerprint binds the committed batch
ledger and project content. Record all three rather than treating a screenshot of the
summary as hash evidence.

## Final packaged/runtime matrix

| Environment | Scenario | Expected result | Status |
| --- | --- | --- | --- |
| Linux packaged | In a level 41 project, dry-run the default level 40 TIE/tfrag/shrub selection, inspect the retained report, then apply | Defaults select exactly the three ready core families; preview and committed batch totals agree | Pending |
| Linux packaged Moby | Separately import one qualified Moby with an internal PVar link, one mapped or cleared external reference, model, native group, and sounds | The complete closure is listed, attached, and rebuilds without the donor | Development level 41 Moby/sound walkthrough passed; exact retained case pending |
| Linux donor removal | Close Forge, make donor ISO/catalog data unavailable, reopen the imported project, preview sounds/assets, and clean-build twice | No donor path is required and repeated hashes match | Automated portability passed; packaged walkthrough pending |
| Linux removal | Remove the Add-mode batch, rebuild, undo and rebuild, then redo and rebuild | Only dependency-safe batch-owned content changes; base/shared content and provenance remain | Automated gate passed; packaged/PCSX2 walkthrough pending |
| Linux + PCSX2 | Patch and exercise imported static/terrain content and the Moby/sounds | Intended content renders and plays; target content and sounds remain intact; level is stable | Development level 05→41/static/terrain/Moby observations passed; exact level 40 capture pending |
| Windows packaged | Transfer the same project ZIP, reopen with donor data unavailable, clean-build, patch, and compare the retained manifest | Project/plan identities and staged/packed hashes agree with Linux | Pending Windows runner |

## Interruption and recovery checklist

| Check | Required result | Current evidence |
| --- | --- | --- |
| Cancel donor hashing/planning | No project, catalog, or donor mutation | Automated pass |
| Reject an over-capacity family | Actionable blocker; no apply authorization | Automated pass |
| Cancel/fail attachment staging | No batch/content publication; newly staged blobs cleaned | Automated pass |
| Stale apply or removal preview | Reject before mutation and require a new preview | Automated pass |
| Interrupt/cancel bake | Restore the prior staging manifest and allow a clean retry | Automated pass |
| Cancel build before patch publication | Preserve the prior development ISO and report that it was unchanged | Automated pass |
| Interrupt packaged save/build process | Reopen the last saved project and retain the last known-good stage/image | Pending packaged walkthrough |
| Remove/undo/redo after reopen | Preserve base/shared content and rebuild deterministically at every state | Automated domain pass; packaged walkthrough pending |

M16-009 remains open until the exact level 40 to level 41 packaged workflow, converted-
donor profile matrix, retained Linux/Windows hash manifests, and PCSX2 captures complete
the pending rows. The existing
development observations reduce runtime risk but do not meet those evidence requirements.
