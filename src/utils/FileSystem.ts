import { randomUUID } from 'node:crypto';
import { access, mkdir, rename, statfs, unlink, writeFile } from 'node:fs/promises';
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
