# Horizon Forge contributor guide

These rules apply to the entire repository unless a more specific `AGENTS.md` overrides them.

## TypeScript organization

- Put reusable, type-only TypeScript contracts in `src/types/`.
- Name type files with PascalCase and the `.d.ts` suffix, for example `ForgeApi.d.ts`.
- Keep `.d.ts` files declarative: no runtime values, functions, classes, or side effects.
- Keep renderer globals and ambient declarations in `src/types/Renderer.d.ts`.
- Use `import type` whenever an import is erased at runtime.
- Import declarations through `.js` specifiers so NodeNext and emitted JavaScript resolve consistently.
- Do not create feature-local `types/` folders. A small prop or private implementation type used by one component/module may remain beside its consumer.

## Utility organization

- Put reusable TypeScript helper functions in `src/utils/`.
- Name utility files with PascalCase and the `.ts` suffix, grouped by concern, for example `FileSystem.ts` or `Format.ts`.
- Do not add catch-all files such as `Utils.ts`, barrel exports, or nested utility folders without a concrete need.
- Keep a one-use helper local. Move it to `src/utils/` when it gains another consumer or clearly represents a cross-feature concern.
- Domain services, stateful stores, protocol codecs, and runtime protocol declarations are not utilities; keep them with their owning subsystem.

## Imports and moves

- Import directly from the owning file rather than through re-export barrels.
- Electron/NodeNext runtime imports use `.js` specifiers. Renderer runtime imports follow the existing `.ts` convention.
- When moving runtime modules, update compiled-path imports in integration tests as well as source imports.
- Preserve unrelated worktree changes.

## Implementation constraints

- Keep classes and modules focused and ideally below 500 logical lines. Treat the limit as a signal to separate responsibilities, not a reason to create one-use abstractions or fragment cohesive code (NFR-MAINT-002).
- Preserve the dependency direction: parsing and transformation belong in the Ratchet PS2 SDK, desktop orchestration in `Forge.Host`, lifecycle and privileged integration in Electron, and presentation in React/Three.js (NFR-MAINT-001).
- Prefer the platform, standard library, existing SDK, and installed dependencies before adding packages. New runtime dependencies require a concrete current use (NFR-MAINT-003).
- Keep import, bake, hash, pack, and patch work off the Electron main and renderer loops. Long operations need progress and cancellation at safe checkpoints (NFR-PERF-004, NFR-REL-003).
- Preserve the last known-good user state. Generated files and manifests must be validated before atomic replacement, and failures must remain actionable (NFR-REL-001, NFR-REL-002).
- Treat IPC payloads, paths, projects, archives, and model data as untrusted. Validate at the boundary and keep Electron context isolation, sandboxing, and navigation restrictions intact (NFR-SEC-001 through NFR-SEC-003).
- Keep interactive UI keyboard accessible, visibly focused, usable with display scaling, and understandable without color alone (NFR-UX-001, NFR-UX-002).

## Verification

- Run `npm run typecheck` after TypeScript or declaration changes.
- Run `npm test` after behavior changes or runtime module moves.
- Run `git diff --check` before handing work back.

## C# boundary

- `src/types/` and `src/utils/` are TypeScript conventions. Keep C# declarations and helpers in the appropriate `Forge.Host` domain or bridge namespace using PascalCase filenames.
