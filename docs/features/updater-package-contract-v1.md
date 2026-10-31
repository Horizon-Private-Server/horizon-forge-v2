# Forge updater package and install contract v1

Status: frozen for M13 implementation; packaged VM qualification remains in M13-006
Owners: release workflow, Electron main process, external activation helper
Requirements: FR-UPD-003 through FR-UPD-005, FR-RELENG-004, NFR-SEC-004

## Boundary

This contract covers full-package Forge updates for x64 Linux and Windows. It does not
authorize delta patches, in-process replacement, package-manager mutation, macOS, or
project/catalog migration. Project and user-data directories are never inside an
application package and are not copied, deleted, or rolled back by the updater.

The current `release-manifest.json` schema 1 remains sufficient for passive discovery
and manually downloading a hash-checked archive. It is not installation authority.
M13-002 introduces the signed schema 2 envelope described below before any downloaded
package can enter `ready-to-install` state.

## Portable package layout

| Platform | Archive | Single top-level directory | Main executable |
| --- | --- | --- | --- |
| Linux x64 | `horizon-forge-<version>-linux-x64.tar.gz` | `horizon-forge-linux-x64` | `horizon-forge` |
| Windows x64 | `horizon-forge-<version>-windows-x64.zip` | `horizon-forge-windows-x64` | `horizon-forge.exe` |

The root contains `forge-package.json`, the Electron distribution, `resources/app`,
and the self-contained `resources/host`. Archives contain regular directories/files
only. Absolute paths, `.`/`..`, backslashes, drive prefixes, links, devices, sockets,
duplicate paths, case-fold collisions, and a file that is also another entry's parent
are invalid.

`forge-package.json` schema 1 declares:

- product, Forge version/channel, source commit, SDK revision, and bridge protocol;
- platform, architecture, archive format, top-level directory, and main executable;
- minimum updater version and source repository/workflow provenance;
- payload regular-file count and expanded byte count, excluding the manifest itself;
- Linux executable paths; and
- every other package file's root-relative POSIX path, exact byte length, and lowercase
  SHA-256. The manifest excludes itself to avoid a circular hash.

The signed release envelope records the exact byte length and SHA-256 of the embedded
package manifest. Extraction validates that hash first, then requires an exact match
between extracted regular files and the embedded list. Extra and missing files fail.

Limits are 4 GiB compressed bytes per artifact, 8 GiB expanded regular-file bytes,
10,000 regular files, 8 MiB for `forge-package.json`, and 512 characters per relative
path. These are validation limits, not packaging targets.

## Signed release envelope

Installable `release-manifest.json` schema 2 retains the schema 1 release/artifact
identity and adds:

- `minimumUpdaterVersion`;
- repository, workflow, workflow-run ID, source commit, and release tag provenance;
- per artifact format, top-level directory, executable, expanded bytes/file count, and
  embedded package-manifest size/SHA-256; and
- detached-signature algorithm, key ID, and signature asset name.

`release-manifest.json.sig` is the raw 64-byte detached Ed25519 signature over the exact
published UTF-8 bytes of `release-manifest.json`. Forge pins DER-SPKI public keys by key
ID in the packaged `forge.updateKeys` application metadata.
Adding a key requires an earlier updater release that trusts it; removal/revocation is
an updater release decision. HTTPS and the exact GitHub repository/release URL remain
mandatory but are not substitutes for the detached signature.

Windows stable packages additionally require the configured Authenticode publisher
before M13-006 can qualify stable self-installation. Nightly Windows builds still need
the detached manifest signature. Linux relies on the detached signature and package
file hashes. Secrets are available only to trusted publishing jobs; pull requests
cannot produce installable signatures.

The CI publisher emits schema 2 only when `FORGE_UPDATE_KEY_ID` and
`FORGE_UPDATE_PUBLIC_KEY_SPKI` repository variables and the
`FORGE_UPDATE_SIGNING_KEY_PEM` release secret are all present. Otherwise it deliberately
retains schema 1/manual handoff. Partial signing configuration fails publishing rather
than producing an envelope that no released client can authenticate.

Stable consumes a strictly newer stable version. Nightly consumes a strictly newer
nightly unless the user explicitly switches channels. A channel switch or downgrade
requires a separate confirmation and is never an automatic-check side effect. Schema 1,
unknown signature keys/algorithms, stale/replayed identities, and newer minimum-updater
requirements may be shown for manual handoff but cannot self-install.

## Install layout classification

Detection is read-only: it examines process/package identity, paths, metadata, and
access permissions without creating a probe file.

| Kind | Meaning | Updater behavior |
| --- | --- | --- |
| `managed` | Stable/nightly portable root with valid manifest; root and parent are replaceable | Full-package flow allowed after signed staging validation |
| `package-manager` | Snap, Flatpak, AppImage, Nix, Store, `/usr`, `/opt`, `/app`, Program Files, or equivalent protected ownership | Open the trusted package/release handoff; never mutate |
| `read-only` | Valid portable release root whose root or parent cannot be replaced | Explain permissions and use manual handoff |
| `development` | Unpackaged process or package with `development` channel | No update installation |
| `unknown` | Missing/invalid marker, indirect root, wrong executable/platform, or unrecognized layout | No mutation; manual handoff only |
| `staged` | Valid release package under Forge's update staging root | Validate/recover/activate; never treat as the active managed root |

The root itself and all path components used for activation must resolve without an
indirect application-root symlink. Access checks are only eligibility hints; the
external helper revalidates ownership, target identity, and permissions immediately
before every mutation.

## Staging, activation, and rollback

Downloads use a unique file below `<userData>/updates/downloads`. Validated extraction
uses `<userData>/updates/staging/<nonce>/<top-level-directory>`. The staging record
contains the signed release identity, package/manifest hashes, source URL, completion
state, and target install fingerprint. Partial downloads/extractions never become a
complete stage and are safe to remove on launch.

Before invoking the platform `tar` extractor, Forge reads bounded TAR.GZ or ZIP metadata
itself and rejects every unsafe entry/layout class above. Linux release archives are
written as USTAR; Windows release ZIPs use ordinary non-ZIP64 entries. Extraction occurs
only inside a newly created nonce directory. Forge then walks the output without
following links and validates the exact raw package manifest, file set, sizes, hashes,
and executable permissions before atomically recording a complete stage.

Active and rollback installations may contain extra regular files left by extracting
a portable ZIP over an older release. Every manifested file must still be present
with its expected size, hash, and executable permissions, and links and special files
remain invalid. Extra files stay in the rollback directory; they are never copied
into the new installation. Stages and candidates always require an exact file set.

Install and restart writes a nonce-bound handoff record and starts a minimal external
helper outside the active application root. The helper:

1. validates the handoff, exact managed target, signed stage, and unchanged install
   fingerprint;
2. waits for the originating Forge PID to exit;
3. copies the stage to a unique sibling candidate on the target filesystem and verifies
   the complete file manifest again;
4. renames the active root to one rollback root, then renames the candidate to the
   active name; and
5. launches the exact declared executable with the nonce/health record path.

The two final directory renames are the activation boundary. If either fails, the
helper restores the old name where possible and reports a recoverable manual action.
It never recursively targets a path supplied only by renderer/IPC input.

The new main process records healthy state only after package identity is validated,
settings open, the host handshake succeeds, and the first editor window is ready.
Missing/failed health before the bounded timeout restores or offers the single rollback
copy on the next helper/Forge launch. A successful health record permits candidate/
download cleanup and retains exactly one rollback until a later successful update.

The packaged helper is a self-contained single-file `Forge.Updater` executable copied
to `<userData>/updates/activation/<nonce>` before it starts. Its handoff must be the
bounded `handoff.json` beside that executable, match the command-line nonce and exact
SHA-256 passed by Forge, and identify complete Forge package manifests at both source
and target. Checkpoints are atomically replaced in `state.json`; an interrupted helper
reconciles the verified active/candidate/rollback roots rather than trusting a
checkpoint alone.

Dirty projects block the quit handoff. Download and extraction never close Forge.
Explicit save, discard, or cancel remains in the ordinary project lifecycle; the
updater cannot write or infer project state.

## Fixtures and qualification matrix

Automated fixtures cover valid manifests plus unsafe paths, case/prefix collisions,
count/size overflow, missing required files, wrong executable/platform, invalid
provenance, development, managed, read-only, external, unknown, and staged layouts.
Package-manifest generation streams file hashing rather than reading the package into
memory.

M13-006 runs these packaged cases before release:

| Platform/layout | Discovery | Self-install | Expected handoff |
| --- | --- | --- | --- |
| Linux extracted portable directory | Yes | Yes | None |
| Linux `/usr`/`/opt`, Snap, Flatpak, AppImage, Nix | Yes | No | Package manager or trusted release |
| Linux valid but read-only portable directory | Yes | No | Permissions/manual release |
| Windows extracted ZIP in a user-owned directory | Yes | Yes | None |
| Windows Program Files/Store or valid but read-only directory | Yes | No | Installer/Store/manual release |
| Development, unknown, tampered, or already-staged root | Passive/manual only | No | Diagnostic or recovery action |

The clean-VM record must include current→newer update, dirty-project refusal,
permission/file-lock failure, process kill/power-loss checkpoints, failed health,
rollback, channel/downgrade rejection, settings/project preservation, and cleanup.

## Threat decisions

| Threat | Required control |
| --- | --- |
| Compromised/replayed release metadata or redirect | Exact repository HTTPS URLs, signed manifest identity, pinned key, newer/channel checks |
| Tampered or swapped archive/stage | Signed archive and embedded-manifest hashes, then complete per-file verification before activation |
| Traversal, link escape, case alias, or archive bomb | Reject unsafe entry types/paths before writes and enforce compressed/expanded/count/path limits |
| Compromised renderer requesting arbitrary replacement | Renderer never supplies raw install/stage paths; main creates a nonce-bound validated handoff |
| Target changed after staging | Record and revalidate the complete active-install fingerprint immediately before mutation |
| Kill, power loss, Windows lock, or failed startup | Sibling candidate, two-rename activation, one rollback, checkpoint state, health confirmation |
| Package-manager/protected ownership | Read-only classification and external trusted handoff; no privilege escalation or write probe |
| Local actor already able to modify the active install | Outside updater integrity guarantees; never weakens signed download/stage validation |
