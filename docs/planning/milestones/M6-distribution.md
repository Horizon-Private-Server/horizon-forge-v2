# M6 — Distribution and P0 release

Priority: P0  
Depends on: M0-M5  
Task register: [M6 tasks](../tasks/M6-tasks.md)

## Outcome

Horizon Forge P0 ships as reproducible Linux and Windows packages through secure
nightly and stable GitHub workflows with update discovery, diagnostics,
documentation, and measured release quality.

## Deliverables

- Linux and Windows application packages with bundled self-contained host/SDK.
- Per-main-commit nightly versions and manual-tag stable releases.
- Checksums, provenance, channel-isolated update metadata, and signing hooks.
- Update UX appropriate to each platform.
- Performance/memory/accessibility baselines and support documentation.
- Recorded P0 release qualification.

## Exit gate

Packaged Linux and Windows builds pass the same portable-project and complete
edit-to-patch workflow; stable users cannot receive nightlies accidentally; and
the P0/MVP gate is recorded as passed.

