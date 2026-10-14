import { randomUUID } from 'node:crypto';
import { access, lstat, mkdir, rename, statfs, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';

export async function fileExists(candidate: string): Promise<boolean> {
  try {
    await access(candidate);
    return true;
  } catch {
    return false;
  }
}

export async function availableBytes(directory: string): Promise<number | undefined> {
  if (!directory) return undefined;
  let candidate = path.resolve(directory);
  while (!await fileExists(candidate)) {
    const parent = path.dirname(candidate);
    if (parent === candidate) return undefined;
    candidate = parent;
  }
  try {
    const fileSystem = await statfs(candidate, { bigint: true });
    return Number(fileSystem.bavail * fileSystem.bsize);
  } catch {
    return undefined;
  }
}

export async function isDirectDirectory(candidate: string): Promise<boolean> {
  const resolved = path.resolve(candidate);
  const root = path.parse(resolved).root;
  let current = root;
  try {
    const rootMetadata = await lstat(current);
    if (!rootMetadata.isDirectory() || rootMetadata.isSymbolicLink()) return false;
    for (const segment of resolved.slice(root.length).split(path.sep).filter(Boolean)) {
      current = path.join(current, segment);
      const metadata = await lstat(current);
      if (!metadata.isDirectory() || metadata.isSymbolicLink()) return false;
    }
    return true;
  } catch {
    return false;
  }
}

export async function isDirectDirectoryWithin(parent: string, candidate: string): Promise<boolean> {
  const resolvedParent = path.resolve(parent);
  const resolvedCandidate = path.resolve(candidate);
  const relative = path.relative(resolvedParent, resolvedCandidate);
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) return false;
  let current = resolvedParent;
  try {
    const parentMetadata = await lstat(current);
    if (!parentMetadata.isDirectory() || parentMetadata.isSymbolicLink()) return false;
    for (const segment of relative.split(path.sep).filter(Boolean)) {
      current = path.join(current, segment);
      const metadata = await lstat(current);
      if (!metadata.isDirectory() || metadata.isSymbolicLink()) return false;
    }
    return true;
  } catch {
    return false;
  }
}

export async function writeJsonSafely(file: string, value: unknown): Promise<void> {
  await mkdir(path.dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.${randomUUID()}.tmp`;
  try {
    await writeFile(temporary, `${JSON.stringify(value, null, 2)}\n`, { encoding: 'utf8', flush: true });
    await rename(temporary, file);
  } catch (error) {
    await unlink(temporary).catch(() => undefined);
    throw error;
  }
}
