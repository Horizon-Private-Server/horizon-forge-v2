# M13 full-package updater qualification

Date: 2026-10-14  
Status: automated fixtures and packaged Linux happy path passed; remaining clean-VM
matrix pending

## Automated gate

Run `npm run test:updates` and `npm run test:bridge:cs`. The updater fixtures verify:

- channel/version ordering, pinned Ed25519 signatures, and exact signed release identity;
- bounded, path-safe package manifests and managed/staged/read-only/external/unknown
  install classification;
- atomic verified downloads, restart revalidation, cancellation, concurrency, redirect,
  corruption, timeout, oversize, and disk-failure handling;
- Linux TAR.GZ and Windows ZIP preflight, extraction, complete staged-package validation,
  tamper recovery, and cleanup of partial stages;
- nonce-bound external activation, startup health, successful Linux replacement, retained
  rollback, failed-health rollback, and interrupted-rename recovery; and
- shared ribbon/notification lifecycle state, progress, defer, retry, install, handoff,
  failure, rollback, and success presentation.

These fixtures support the matrix but do not replace packaged clean-VM evidence.

## Packaged clean-VM matrix

Do not commit user settings, projects, downloaded packages, update state, or local paths.
Retain the source and target Forge revisions, platform/OS version, install layout,
release-manifest identity, result, and redacted diagnostic/log reference for each row.

| Environment | Scenario | Expected result | Status |
| --- | --- | --- | --- |
| Linux managed portable | Discover, download, stage, install, relaunch, and confirm the newer signed nightly | The update completes and the relaunched app reports the new identity as healthy. | Passed in a packaged walkthrough reported 2026-10-14; revision/log reference not retained in this repository |
| Linux managed portable | Repeat an update with existing settings, catalog entries, projects, and one prior known-good package | User data remains intact and exactly one known-good rollback is retained. | Pending retained evidence |
| Linux managed portable | Start with a dirty project and choose cancel, save-and-restart, then discard-and-restart in separate runs | Cancel keeps Forge open; save/discard uses the normal project lifecycle; update never infers or writes project state. | Pending |
| Linux managed portable | Inject failed startup health after activation | The prior package is restored and relaunched with an actionable rollback result. | Automated helper fixture passed; packaged evidence pending |
| Linux managed portable | Interrupt download, extraction, candidate copy, first rename, second rename, and health wait | Each checkpoint leaves a recoverable old or new install and no project/user-data mutation. | Download/extraction/interrupted-rename fixtures passed; packaged checkpoint evidence pending |
| Linux unsupported layouts | Check `/usr`/`/opt`, read-only portable, Snap, Flatpak, AppImage, and Nix-style installs | Forge never self-modifies and offers the documented trusted package-manager/manual handoff. | Classification fixtures passed; packaged evidence pending |
| Windows managed portable | Update an extracted user-owned ZIP, including one run with a transient file lock | The newer signed package relaunches healthy; a lock failure preserves the prior install and reports recovery guidance. | Package staging fixture passed; clean-VM activation evidence pending |
| Windows unsupported layouts | Check Program Files, Store, and read-only portable installs | Forge never self-modifies and offers the documented installer/Store/manual handoff. | Classification fixtures passed; packaged evidence pending |
| Linux and Windows | Offer downgrade, replayed/stale metadata, wrong channel/platform/architecture, bad signature, archive tamper, and stage tamper | Every invalid candidate is rejected before active-install mutation with bounded diagnostics. | Automated threat fixtures passed; packaged signed-fixture evidence pending |

## Remaining gate

M13-001 and M13-006 remain open until the Windows managed-install row and the retained
rollback, interruption, unsupported-layout, preservation, and signed-threat evidence are
complete. The successful Linux self-update closes the M13-005 packaged UI/lifecycle
walkthrough; it does not by itself qualify the full updater release gate.
