import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { lstat, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';

import type { ForgePackageManifest } from '../types/Updates.js';
import { isDirectDirectory, isDirectDirectoryWithin } from '../utils/FileSystem.js';
import { validateForgePackageManifest } from '../utils/UpdateValidation.js';
import type { VerifiedUpdateDownload } from './UpdateDownloadManager.js';

export async function validateStagedPackage(
  root: string,
  download: VerifiedUpdateDownload,
  platform: 'linux' | 'windows',
  signal?: AbortSignal,
  progress?: (completed: number) => void,
  stagingRoot?: string,
): Promise<ForgePackageManifest> {
  if (!await (stagingRoot ? isDirectDirectoryWithin(stagingRoot, root) : isDirectDirectory(root))) {
    throw new Error('Extracted update root is indirect or invalid.');
  }
  const manifestPath = path.join(root, 'forge-package.json');
  const manifestMetadata = await lstat(manifestPath);
  const artifact = download.envelope.artifact;
  if (!manifestMetadata.isFile() || manifestMetadata.isSymbolicLink()
    || manifestMetadata.size !== artifact.packageManifestSize) {
    throw new Error('Extracted package manifest size does not match signed metadata.');
  }
  const manifestBytes = await readFile(manifestPath);
  if (createHash('sha256').update(manifestBytes).digest('hex') !== artifact.packageManifestSha256) {
    throw new Error('Extracted package manifest hash does not match signed metadata.');
  }
  const manifest = parsePackageManifest(manifestBytes, 'Extracted package');
  assertManifestMatchesEnvelope(manifest, download);
  await validatePackageFiles(root, manifest, 'Extracted package', signal, progress);
  return manifest;
}

export async function fingerprintInstalledPackage(
  root: string,
  platform: 'linux' | 'windows',
  signal?: AbortSignal,
): Promise<string> {
  await assertDirectRoot(root, 'Active installation');
  const manifestPath = path.join(root, 'forge-package.json');
  const metadata = await lstat(manifestPath);
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.size <= 0 || metadata.size > 8 * 1024 ** 2) {
    throw new Error('Active installation package manifest is invalid.');
  }
  const bytes = await readFile(manifestPath);
  const manifest = parsePackageManifest(bytes, 'Active installation package');
  if (manifest.platform !== platform) throw new Error('Active installation platform identity is invalid.');
  await validatePackageFiles(root, manifest, 'Active installation', signal);
  return createHash('sha256').update(bytes).digest('hex');
}

async function assertDirectRoot(root: string, label: string): Promise<void> {
  if (!await isDirectDirectory(root)) {
    throw new Error(`${label} root is indirect or invalid.`);
  }
}

function parsePackageManifest(bytes: Uint8Array, label: string): ForgePackageManifest {
  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    throw new Error(`${label} manifest is not valid UTF-8 JSON.`);
  }
  return validateForgePackageManifest(value);
}

async function validatePackageFiles(
  root: string,
  manifest: ForgePackageManifest,
  label: string,
  signal?: AbortSignal,
  progress?: (completed: number) => void,
): Promise<void> {
  const files = await collectRegularFiles(root, signal);
  if (files.length !== manifest.fileCount) throw new Error(`${label} contains extra or missing files.`);
  const actualPaths = files.map((file) => file.relativePath);
  const expectedPaths = manifest.files.map((file) => file.path);
  if (actualPaths.some((filePath, index) => filePath !== expectedPaths[index])) {
    throw new Error(`${label} file set does not match its manifest.`);
  }
  const executablePaths: string[] = [];
  for (let index = 0; index < files.length; index += 1) {
    throwIfAborted(signal);
    const actual = files[index];
    const expected = manifest.files[index];
    if (actual.size !== expected.size || await hashFile(actual.absolutePath, signal) !== expected.sha256) {
      throw new Error(`${label} file does not match its manifest: ${actual.relativePath}`);
    }
    if (manifest.platform === 'linux' && (actual.mode & 0o111) !== 0) executablePaths.push(actual.relativePath);
    progress?.(index + 1);
  }
  if (manifest.platform === 'linux'
    && (executablePaths.length !== manifest.executablePaths.length
      || executablePaths.some((filePath, index) => filePath !== manifest.executablePaths[index]))) {
    throw new Error(`${label} executable permissions do not match its manifest.`);
  }
}

async function collectRegularFiles(root: string, signal?: AbortSignal): Promise<{
  absolutePath: string;
  relativePath: string;
  size: number;
  mode: number;
}[]> {
  const files: { absolutePath: string; relativePath: string; size: number; mode: number }[] = [];
  const visit = async (directory: string): Promise<void> => {
    throwIfAborted(signal);
    const entries = await readdir(directory, { withFileTypes: true });
    entries.sort((left, right) => left.name.localeCompare(right.name));
    for (const entry of entries) {
      throwIfAborted(signal);
      const absolutePath = path.join(directory, entry.name);
      const metadata = await lstat(absolutePath);
      if (metadata.isSymbolicLink()) throw new Error('Package contains a symbolic link.');
      if (metadata.isDirectory()) {
        await visit(absolutePath);
      } else if (metadata.isFile()) {
        const relativePath = path.relative(root, absolutePath).split(path.sep).join('/');
        if (relativePath !== 'forge-package.json') {
          files.push({ absolutePath, relativePath, size: metadata.size, mode: metadata.mode });
          if (files.length > 10_000) throw new Error('Package contains too many files.');
        }
      } else {
        throw new Error('Package contains a special file.');
      }
    }
  };
  await visit(root);
  files.sort((left, right) => left.relativePath < right.relativePath ? -1 : left.relativePath > right.relativePath ? 1 : 0);
  return files;
}

function assertManifestMatchesEnvelope(manifest: ForgePackageManifest, download: VerifiedUpdateDownload): void {
  const release = download.envelope.manifest;
  const artifact = download.envelope.artifact;
  if (manifest.version !== release.version
    || manifest.channel !== release.channel
    || manifest.commit !== release.commit
    || manifest.sdkRevision !== release.sdkRevision
    || manifest.bridgeProtocol !== release.bridgeProtocol
    || manifest.minimumUpdaterVersion !== release.minimumUpdaterVersion
    || manifest.platform !== artifact.platform
    || manifest.architecture !== artifact.architecture
    || manifest.format !== artifact.format
    || manifest.topLevelDirectory !== artifact.topLevelDirectory
    || manifest.executable !== artifact.executable
    || manifest.expandedSize !== artifact.expandedSize
    || manifest.fileCount !== artifact.fileCount
    || manifest.provenance.repository !== release.provenance.repository
    || manifest.provenance.workflow !== release.provenance.workflow) {
    throw new Error('Extracted package identity does not match its signed release envelope.');
  }
}

async function hashFile(filePath: string, signal?: AbortSignal): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath, { signal })) hash.update(chunk);
  return hash.digest('hex');
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw signal.reason ?? new Error('Update staging cancelled.');
}
