# M4 — Build and rapid patch loop

Priority: P0  
Depends on: M3, M3A

Task register: [M4 tasks](../tasks/M4-tasks.md)

## Outcome

One command validates, incrementally bakes, builds the UYA map WAD, patches the
separate development ISO recoverably, verifies it, and reports that external
PCSX2 may be reloaded.

## Deliverables

- Staging integration with the SDK-owned WAD/archive packer proven in M3A.
- Development target validation and patch planning.
- Journaled in-place patching and full-image transactional fallback.
- One-click orchestration with progress/cancellation/error reporting.
- Linux and Windows manual emulator-loop evidence.

## Exit gate

Repeated edits require no manual WAD/ISO movement; wrong or clean source ISOs are
rejected; interrupted patch scenarios recover; and successful output loads in an
externally managed PCSX2 session.
