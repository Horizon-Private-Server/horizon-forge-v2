import { constants } from 'node:fs';
import { access, lstat, readFile } from 'node:fs/promises';
import path from 'node:path';

import type { ForgePackageManifest, InstallLayoutAssessment } from '../types/Updates.js';
import { isDirectDirectory } from '../utils/FileSystem.js';
import { classifyInstallLayout, validateForgePackageManifest } from '../utils/UpdateValidation.js';

const packageManifestName = 'forge-package.json';
const maximumManifestSize = 8 * 1024 * 1024;

export interface InstallLayoutDetectionOptions {
  isPackaged: boolean;
  execPath: string;
  stagingRoot: string;
  platform: NodeJS.Platform;
  environment?: NodeJS.ProcessEnv;
}

interface InstallLayoutDependencies {
  canReplace(root: string): Promise<boolean>;
}

const defaultDependencies: InstallLayoutDependencies = {
  async canReplace(root) {
    try {
      await Promise.all([
        access(root, constants.R_OK | constants.W_OK),
        access(path.dirname(root), constants.R_OK | constants.W_OK),
      ]);
      return true;
    } catch {
      return false;
    }
  },
};

export async function detectInstallLayout(
  options: InstallLayoutDetectionOptions,
  dependencies: InstallLayoutDependencies = defaultDependencies,
): Promise<InstallLayoutAssessment> {
  const root = path.resolve(path.dirname(options.execPath));
  if (!options.isPackaged) return classifyInstallLayout({ root, isPackaged: false });
  const externalReason = externalInstallReason(root, options.platform, options.environment ?? process.env);
  if (externalReason) return classifyInstallLayout({ root, isPackaged: true, externalReason });

  const loaded = await readPackageManifest(root);
  if ('error' in loaded) return classifyInstallLayout({ root, isPackaged: true, manifestError: loaded.error });
  const manifest = loaded.manifest;
  const expectedPlatform = options.platform === 'win32' ? 'windows' : options.platform === 'linux' ? 'linux' : undefined;
  const identityMatches = !!expectedPlatform
    && manifest.platform === expectedPlatform
    && path.basename(options.execPath) === manifest.executable;
  const safeRoot = await isDirectDirectory(root);
  return classifyInstallLayout({
    root,
    isPackaged: true,
    manifest,
    identityMatches,
    safeRoot,
    staged: isWithin(options.stagingRoot, root),
    replaceable: await dependencies.canReplace(root),
  });
}

async function readPackageManifest(root: string): Promise<
  { manifest: ForgePackageManifest } | { error: string }
> {
  const manifestPath = path.join(root, packageManifestName);
  try {
    const metadata = await lstat(manifestPath);
    if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.size <= 0 || metadata.size > maximumManifestSize) {
      return { error: 'The installation has an invalid Forge package manifest file.' };
    }
    return { manifest: validateForgePackageManifest(JSON.parse(await readFile(manifestPath, 'utf8'))) };
  } catch (error) {
    return {
      error: error instanceof SyntaxError || error instanceof TypeError
        ? `The installation package manifest is invalid: ${error.message}`
        : 'The installation has no readable Forge package manifest.',
    };
  }
}

function externalInstallReason(root: string, platform: NodeJS.Platform, environment: NodeJS.ProcessEnv): string | undefined {
  if (environment.SNAP) return 'Snap installations are updated by Snap.';
  if (environment.FLATPAK_ID) return 'Flatpak installations are updated by Flatpak.';
  if (environment.APPIMAGE) return 'AppImage installations use their external update workflow.';
  if (environment.NIX_STORE) return 'Nix installations are updated by Nix.';
  if (platform === 'linux' && ['/usr', '/opt', '/app', '/nix/store'].some((prefix) => isWithin(prefix, root))) {
    return 'This protected Linux installation is updated by its installer or package manager.';
  }
  if (platform === 'win32') {
    const prefixes = [environment.ProgramFiles, environment['ProgramFiles(x86)']].filter((value): value is string => !!value);
    if (prefixes.some((prefix) => isWithin(prefix, root))) {
      return 'This protected Windows installation is updated by its installer or package manager.';
    }
    const windowsApps = environment.LOCALAPPDATA && path.join(environment.LOCALAPPDATA, 'Microsoft', 'WindowsApps');
    if (windowsApps && isWithin(windowsApps, root)) return 'Microsoft Store installations are updated by the Store.';
  }
  return undefined;
}

function isWithin(parent: string, candidate: string): boolean {
  const relative = path.relative(path.resolve(parent), path.resolve(candidate));
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}
