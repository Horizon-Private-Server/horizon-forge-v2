# M0 — Foundations and walking skeleton

Priority: P0  
Task register: [M0 tasks](../tasks/M0-tasks.md)

## Outcome

A packaged Linux Electron application starts the pinned .NET host, completes a
versioned binary handshake, and renders a representative migrated UYA scene using
the backend selected by measured WebGL/WebGPU evidence.

## Entry criteria

- Forge specification accepted as the planning baseline.
- ratchet-ps2-cli and ratchet-map-o-matic are locally available for inspection.
- No production project or data compatibility promise exists yet.

## Deliverables

- Repository/application skeleton and dependency boundaries.
- Repeatable Ratchet SDK bootstrap.
- Binary protocol, host lifecycle, and secure preload boundary.
- Version-zero project/entity/asset identity decisions and fixtures.
- [map-o-matic reuse audit](../../adr/0001-map-o-matic-reuse.md) and
  [WebGL renderer decision](../../adr/0002-webgl-p0-renderer.md).
- Packaged Linux walking skeleton and Linux/Windows CI checks.

## Exit gate

The packaged Linux app—not only the Vite development server—opens the selected
viewport, starts the exact pinned host/SDK, survives a forced host crash, and has a
reproducible renderer benchmark report.

## Risks contained here

- Cross-process framing/backpressure failures.
- Electron renderer privilege leakage.
- Assuming map-o-matic or WebGPU performance without measurement.
- CI depending on an undeclared sibling checkout.
