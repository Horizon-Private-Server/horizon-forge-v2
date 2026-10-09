# M8 HUD bake qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Date: 2026-10-08  
Status: automated Linux gate passed; packaged Windows and PCSX2 visual checks pending

## Automated gate

Run `npm test`. The synthetic end-to-end fixture verifies:

- an unedited HUD takes the byte-preserving path;
- replacing an existing icon texture and appending sprite `E001` invalidates only the
  HUD layer;
- only the HUD header and physical banks 0 and 2 are selected for archive replacement;
- the original sprite ID and frame index remain stable, while the edited frame alone is
  remapped to isolated appended palette and texture records;
- the replacement and appended palette/pixels survive archive packing and semantic
  re-read, with the appended texture placed in bank 2;
- untouched physical banks remain byte-identical;
- a forced clean HUD rebuild has the same staged output fingerprint, and repeated
  packing has the same archive SHA-256; and
- candidate staging is semantically reread before atomic commit, using the staging
  store's tested last-known-good rollback behavior.

Project command tests separately verify ZIP transfer, recovery, exact override
resolution, missing-blob rejection, and cleanup of custom replacement/addition assets.

## Manual packaged-app matrix

| Platform | Scenario | Expected result | Status |
| --- | --- | --- | --- |
| Linux | Replace one visible retail HUD icon and append one `Exxx` icon, then Build | Only HUD reports rebuilt; the development ISO verifies successfully. | Pending packaged walkthrough |
| Linux + PCSX2 | Reload the patched development ISO and exercise both sprite IDs | The retail sprite uses the replacement and the new sprite resolves the appended texture without corrupting other HUD icons. | Pending visual fixture/hook |
| Windows | Repeat clean rebuild and project ZIP transfer in the packaged nightly | Output hashes agree with Linux and only custom attached PIF assets travel with the project. | Pending CI/package run |

M8-005 remains in progress until each pending row records the Forge build, platform,
tester/date, and retained local build/patch log reference.
