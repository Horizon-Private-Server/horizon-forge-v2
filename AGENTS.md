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

## Verification

- Run `npm run typecheck` after TypeScript or declaration changes.
- Run `npm test` after behavior changes or runtime module moves.
- Run `git diff --check` before handing work back.

## C# boundary

- `src/types/` and `src/utils/` are TypeScript conventions. Keep C# declarations and helpers in the appropriate `Forge.Host` domain or bridge namespace using PascalCase filenames.
