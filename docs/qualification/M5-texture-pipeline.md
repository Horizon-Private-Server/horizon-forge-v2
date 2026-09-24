# M5 vanilla texture pipeline qualification

Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Status: automated gate passed; packaged Linux and Windows PCSX2 rendering remains pending

## Automated gate

Run `npm test` on Linux and Windows. The synthetic end-to-end fixture passes a moby,
a shrub, and a cross-level tie through catalog resolution, inventory, exact palette
optimization, bake, WAD pack, and development-ISO patching. It then reopens the ISO
and verifies:

- the installed level WAD matches the build result SHA-256;
- every decoded base texel retains its original PS2 RGBA value;
- all three asset families share one palette instead of three;
- imported quantization error remains zero;
- a repeated no-op build produces the identical WAD SHA-256; and
- cancellation and injected failures preserve the previous staging and ISO state.

The SDK fixture separately verifies base and mip texel order exhaustively, reserved
indexes, incompatible formats, known optimal cases, deterministic heuristic cases,
and malformed input rejection.

## Manual PCSX2 gate

Use the packaged-app procedure in the
[M4 external PCSX2 loop qualification](M4-pcsx2-test-loop.md). For at least one map
containing visible moby, tie, and shrub textures on both Linux and Windows, record:

- packaged Forge and PCSX2 versions;
- level number and edited asset class IDs without retaining game data or paths;
- before/after palette counts and estimated VRAM bytes from staging diagnostics;
- screenshots confirming correct color, alpha, and material assignment; and
- the local `build-patch.log` reference and pass/fail result.

M5-005 remains open until both platform records pass without texture corruption or
palette-count regression.
