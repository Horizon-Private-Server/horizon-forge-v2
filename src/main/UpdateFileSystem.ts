import * as nodeFileSystem from 'node:fs';
import { createRequire } from 'node:module';

// Electron's patched fs exposes ASAR files as directories; updater storage must use physical files.
export const updateFileSystem: typeof nodeFileSystem = process.versions.electron
  ? createRequire(import.meta.url)('original-fs') as typeof nodeFileSystem
  : nodeFileSystem;
