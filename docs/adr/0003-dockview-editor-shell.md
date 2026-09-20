# ADR-0003: Dockview editor shell

- Status: Accepted
- Date: 2026-09-20

## Context

Forge needs IDE-style panels that can tab, resize, move, hide, float, restore, and
remain keyboard accessible. Implementing a docking engine would add substantial
input, layout, focus, serialization, and accessibility risk unrelated to map
editing.

## Decision

Use the MIT-licensed `dockview-react` 8.3.1 package for the editor shell. It
supports React 19, has no external runtime dependency beyond its own framework
packages, provides keyboard navigation and screen-reader announcements, and
serializes docked and floating layouts through `toJSON`/`fromJSON`.

Forge persists that JSON in the flat machine-specific `ui.editorLayout` setting.
Only registered panel IDs and matching component names restore. Missing panels are
valid because a user may have hidden them; unknown or renamed definitions reset to
the current default instead of preventing the editor from opening.

Forge supports Dockview's in-window floating groups as detachable panels. External
browser popout windows remain disabled because Electron's deny-by-default window
policy intentionally blocks them; they can be assessed separately if native
multi-window editing becomes a requirement.

## Consequences

- Docking behavior, drag targets, focus movement, tabs, and layout serialization
  come from one maintained library rather than Forge code.
- The View menu reopens hidden panels and resets the complete layout.
- Panel contents remain ordinary React/Mantine components and receive editor state
  from the typed runtime context.
- A Dockview major-version upgrade requires a saved-layout compatibility check.

References: [Dockview introduction](https://dockview.dev/docs/overview/introduction/),
[saving state](https://dockview.dev/docs/core/state/save), and
[keyboard navigation](https://dockview.dev/docs/releases/whats-new/whats-new-v7/).
