# Moby simulation lifecycle v1

Status: frozen for M17-001  
Scope: editor-only derived rendering; never project state or baked output

## Boundary

`MobySimulationRegistry` selects a factory by normalized game and native Moby class.
Shared scene code contains no class or game switch. A factory receives only a copied,
read-only project snapshot, the instances for its class, and renderer services for the
scene root, camera, resolved assets/FX, proxy visibility, selection binding, and
diagnostics. It cannot execute editor commands, save, build, bake, or patch.

The project snapshot owns authoritative transforms, PVars, and references. Simulators
must not mutate or retain a mutable project buffer. Resolved asset and FX handles are
shared-cache leases: the simulator releases every handle it acquires when disposed.
The simulator owns every derived object it adds below the supplied root. The controller
owns proxy suppression and selection bindings, so either is always unwound on failure.

## State and timing

```text
disabled --enable--> creating --> enabled <--> paused
    ^                    |           |
    +------disable-------+-----------+

enabled --context lost/project switch--> dispose --> recreate when available
enabled --class invalidation(recreate)--> dispose class --> recreate class
enabled --simulator error-------------> dispose class --> ordinary proxy fallback
```

- Disabled is the initial state and owns no simulator or derived resources.
- `create` is the registered factory. A late asynchronous result is disposed if its
  generation, project, enabled state, or WebGL context is no longer current.
- `enable`, fixed update, render update, reset, disable, and dispose are explicit.
- Fixed updates use a deterministic step (default 1/60 second). At most five steps are
  processed per rendered frame by default; excess wall time is counted and dropped.
- Pause keeps the current derived preview visible but advances neither fixed nor render
  time. Reset clears accumulated time/ticks and calls `reset`.
- Disabling disposes everything. A project switch resets time and recreates registered
  classes from the new snapshot.

## Invalidation and failure

Committed transform, PVar, reference, asset, FX, and visibility changes carry explicit
invalidation events plus a replacement immutable snapshot. A simulator either handles
the event or requests recreation of only its class.

Factories returning `null` report unsupported data and leave ordinary proxies visible.
An exception during create, enable, update, render, reset, or invalidation is isolated
to that class. Its derived resources and selection bindings are released, its proxy is
restored, and a per-entity actionable diagnostic is reported. Other classes continue.

WebGL context loss immediately disposes all derived resources and restores proxies.
No simulator is recreated until context restoration. Controller statistics expose
registered/active classes, simulated entities, fixed ticks, dropped time, hidden
proxies, and selection bindings so repeated lifecycle tests can return to baseline.

The viewport owns the controller and exposes one compact **Simulation** popover. Its
enable, pause/resume, and reset controls are keyboard accessible and explicitly labeled
preview-only. Enable state lasts only for the current open project and resets on project
switch; it is never serialized. Renderer frames drive the bounded fixed step, while
committed entity/reference/FX snapshots emit narrow invalidations. Graphics-context
loss and viewport disposal release the same controller-owned state.

## Selection and visibility

Each derived pickable object binds to an authoritative entity ID. Selection, focus,
Properties, tree identity, and edits therefore remain attached to the project Moby;
derived objects never become editor entities. Proxy suppression is a renderer overlay:
restoring a proxy means returning control to ordinary hidden/disabled/layer rules, not
forcing the underlying project entity visible.
