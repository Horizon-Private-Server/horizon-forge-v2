import { createReadStream } from 'node:fs';
import { open, stat } from 'node:fs/promises';
import { createGunzip } from 'node:zlib';

import type { InstallableReleaseArtifact } from '../types/Updates.js';

const blockSize = 512;
const maximumArchiveEntries = 20_002;
const maximumExtensionBytes = 8 * 1024;
const maximumCentralDirectoryBytes = 8 * 1024 * 1024;
const maximumCompressionRatio = 200;

export interface UpdateArchiveEntry {
  path: string;
  kind: 'file' | 'directory';
  size: number;
}

export interface UpdateArchiveInspection {
  entries: UpdateArchiveEntry[];
  expandedSize: number;
}

export async function inspectUpdateArchive(
  archivePath: string,
  artifact: InstallableReleaseArtifact,
  signal?: AbortSignal,
): Promise<UpdateArchiveInspection> {
  const entries = artifact.format === 'tar.gz'
    ? await inspectTarGzip(archivePath, artifact, signal)
    : await inspectZip(archivePath, signal);
  return validateArchiveLayout(entries, artifact);
}

export function validateArchiveLayout(
  entries: readonly UpdateArchiveEntry[],
  artifact: InstallableReleaseArtifact,
): UpdateArchiveInspection {
  if (entries.length === 0 || entries.length > maximumArchiveEntries) {
    throw new Error('Update archive contains an invalid number of entries.');
  }
  const normalized = entries.map((entry) => ({ ...entry, path: validateArchivePath(entry.path) }));
  const folded = new Set<string>();
  const files = new Set<string>();
  let expandedSize = 0;
  let fileCount = 0;
  for (const entry of normalized) {
    const lower = entry.path.toLowerCase();
    if (folded.has(lower)) throw new Error(`Update archive contains a duplicate or case-colliding path: ${entry.path}`);
    folded.add(lower);
    if (entry.path !== artifact.topLevelDirectory
      && !entry.path.startsWith(`${artifact.topLevelDirectory}/`)) {
      throw new Error(`Update archive contains an unexpected root: ${entry.path}`);
    }
    if (entry.kind === 'file') {
      fileCount += 1;
      expandedSize += entry.size;
      files.add(lower);
      if (!Number.isSafeInteger(expandedSize) || expandedSize > 8 * 1024 ** 3 + 8 * 1024 ** 2) {
        throw new Error('Update archive exceeds its expanded-size limit.');
      }
    } else if (entry.size !== 0) {
      throw new Error(`Update archive directory has data: ${entry.path}`);
    }
  }
  for (const entry of normalized) {
    const segments = entry.path.toLowerCase().split('/');
    for (let length = 1; length < segments.length; length += 1) {
      if (files.has(segments.slice(0, length).join('/'))) {
        throw new Error(`Update archive path descends through a file: ${entry.path}`);
      }
    }
  }
  const expectedExpandedSize = artifact.expandedSize + artifact.packageManifestSize;
  if (fileCount !== artifact.fileCount + 1 || expandedSize !== expectedExpandedSize) {
    throw new Error('Update archive file count or expanded size does not match signed metadata.');
  }
  if (expandedSize > artifact.size * maximumCompressionRatio) {
    throw new Error('Update archive compression ratio exceeds the safety limit.');
  }
  if (!files.has(`${artifact.topLevelDirectory.toLowerCase()}/forge-package.json`)) {
    throw new Error('Update archive has no embedded Forge package manifest.');
  }
  return { entries: normalized, expandedSize };
}

async function inspectTarGzip(
  archivePath: string,
  artifact: InstallableReleaseArtifact,
  signal?: AbortSignal,
): Promise<UpdateArchiveEntry[]> {
  const stream = createReadStream(archivePath, { signal }).pipe(createGunzip());
  const reader = new StreamByteReader(stream[Symbol.asyncIterator](), signal);
  const entries: UpdateArchiveEntry[] = [];
  let pendingPath: string | undefined;
  let ended = false;
  let expandedSize = 0;
  let fileCount = 0;
  const foldedPaths = new Set<string>();
  while (true) {
    const header = await reader.readExact(blockSize, true);
    if (!header) break;
    if (isZeroBlock(header)) {
      ended = true;
      break;
    }
    validateTarChecksum(header);
    const type = String.fromCharCode(header[156] || 0);
    const size = parseTarNumber(header.subarray(124, 136));
    const headerPath = tarText(header.subarray(0, 100));
    const prefix = tarText(header.subarray(345, 500));
    const joinedPath = prefix ? `${prefix}/${headerPath}` : headerPath;
    if (type === 'L' || type === 'x') {
      if (size <= 0 || size > maximumExtensionBytes) throw new Error('Update TAR metadata is too large.');
      const metadata = (await reader.readExact(size))!;
      pendingPath = type === 'L' ? tarText(metadata) : parsePaxPath(metadata);
      await reader.skip(paddedSize(size) - size);
      continue;
    }
    if (type !== '\0' && type !== '0' && type !== '5') {
      throw new Error(`Update TAR contains an unsupported entry type ${JSON.stringify(type)}.`);
    }
    const rawEntryPath = pendingPath ?? joinedPath;
    pendingPath = undefined;
    const entryPath = type === '5' && rawEntryPath.endsWith('/') ? rawEntryPath.slice(0, -1) : rawEntryPath;
    const validatedPath = validateArchivePath(entryPath);
    if (validatedPath !== artifact.topLevelDirectory
      && !validatedPath.startsWith(`${artifact.topLevelDirectory}/`)) {
      throw new Error(`Update archive contains an unexpected root: ${validatedPath}`);
    }
    const foldedPath = validatedPath.toLowerCase();
    if (foldedPaths.has(foldedPath)) {
      throw new Error(`Update archive contains a duplicate or case-colliding path: ${validatedPath}`);
    }
    foldedPaths.add(foldedPath);
    if (type !== '5') {
      fileCount += 1;
      expandedSize += size;
      if (fileCount > artifact.fileCount + 1
        || expandedSize > artifact.expandedSize + artifact.packageManifestSize
        || expandedSize > artifact.size * maximumCompressionRatio) {
        throw new Error('Update TAR exceeds its signed file or expanded-size limits.');
      }
    }
    entries.push({ path: validatedPath, kind: type === '5' ? 'directory' : 'file', size });
    if (entries.length > maximumArchiveEntries) throw new Error('Update TAR contains too many entries.');
    await reader.skip(paddedSize(size));
  }
  if (!ended || pendingPath) throw new Error('Update TAR ended before its terminator.');
  const secondTerminator = await reader.readExact(blockSize, true);
  if (!secondTerminator || !isZeroBlock(secondTerminator)) throw new Error('Update TAR has an invalid terminator.');
  let trailing: Buffer | undefined;
  while ((trailing = await reader.readExact(blockSize, true))) {
    if (!isZeroBlock(trailing)) throw new Error('Update TAR contains trailing archive data.');
  }
  return entries;
}

async function inspectZip(archivePath: string, signal?: AbortSignal): Promise<UpdateArchiveEntry[]> {
  const metadata = await stat(archivePath);
  const handle = await open(archivePath, 'r');
  try {
    throwIfAborted(signal);
    const tailSize = Math.min(metadata.size, 65_557);
    const tail = await readAt(handle, metadata.size - tailSize, tailSize);
    const eocdOffset = findSignatureBackwards(tail, 0x06054b50);
    if (eocdOffset < 0 || eocdOffset + 22 > tail.length) throw new Error('Update ZIP has no valid end record.');
    if (tail.readUInt16LE(eocdOffset + 4) !== 0 || tail.readUInt16LE(eocdOffset + 6) !== 0) {
      throw new Error('Multi-disk update ZIP files are not supported.');
    }
    const entryCount = tail.readUInt16LE(eocdOffset + 10);
    const centralSize = tail.readUInt32LE(eocdOffset + 12);
    const centralOffset = tail.readUInt32LE(eocdOffset + 16);
    if (entryCount === 0xffff || centralSize === 0xffffffff || centralOffset === 0xffffffff) {
      throw new Error('ZIP64 update packages are not supported.');
    }
    if (entryCount === 0 || entryCount > maximumArchiveEntries
      || centralSize > maximumCentralDirectoryBytes
      || centralOffset + centralSize > metadata.size) {
      throw new Error('Update ZIP central directory exceeds its limits.');
    }
    const central = await readAt(handle, centralOffset, centralSize);
    const entries: UpdateArchiveEntry[] = [];
    let offset = 0;
    for (let index = 0; index < entryCount; index += 1) {
      throwIfAborted(signal);
      if (offset + 46 > central.length || central.readUInt32LE(offset) !== 0x02014b50) {
        throw new Error('Update ZIP central directory is malformed.');
      }
      const versionMade = central.readUInt16LE(offset + 4);
      const flags = central.readUInt16LE(offset + 8);
      const method = central.readUInt16LE(offset + 10);
      const compressedSize = central.readUInt32LE(offset + 20);
      const size = central.readUInt32LE(offset + 24);
      const nameLength = central.readUInt16LE(offset + 28);
      const extraLength = central.readUInt16LE(offset + 30);
      const commentLength = central.readUInt16LE(offset + 32);
      const externalAttributes = central.readUInt32LE(offset + 38);
      const localOffset = central.readUInt32LE(offset + 42);
      const end = offset + 46 + nameLength + extraLength + commentLength;
      if (end > central.length || nameLength === 0) throw new Error('Update ZIP entry is truncated.');
      const name = decodeUtf8(central.subarray(offset + 46, offset + 46 + nameLength));
      const directory = name.endsWith('/');
      const unixMode = (versionMade >>> 8) === 3 ? externalAttributes >>> 16 : 0;
      const unixType = unixMode & 0o170000;
      if ((flags & ((1 << 0) | (1 << 5) | (1 << 6) | (1 << 13))) !== 0
        || compressedSize === 0xffffffff || size === 0xffffffff || localOffset === 0xffffffff
        || (!directory && method !== 0 && method !== 8)
        || (unixType !== 0 && unixType !== (directory ? 0o040000 : 0o100000))) {
        throw new Error(`Update ZIP contains an encrypted, linked, special, or unsupported entry: ${name}`);
      }
      if (directory && (compressedSize !== 0 || size !== 0)) throw new Error(`Update ZIP directory has data: ${name}`);
      await validateZipLocalHeader(handle, localOffset, name, flags, method, compressedSize, centralOffset);
      entries.push({ path: directory ? name.slice(0, -1) : name, kind: directory ? 'directory' : 'file', size });
      offset = end;
    }
    if (offset !== central.length) throw new Error('Update ZIP central directory has trailing records.');
    return entries;
  } finally {
    await handle.close();
  }
}

async function validateZipLocalHeader(
  handle: Awaited<ReturnType<typeof open>>,
  offset: number,
  expectedName: string,
  expectedFlags: number,
  expectedMethod: number,
  compressedSize: number,
  centralOffset: number,
): Promise<void> {
  if (offset + 30 > centralOffset) throw new Error('Update ZIP local header overlaps its central directory.');
  const header = await readAt(handle, offset, 30);
  if (header.readUInt32LE(0) !== 0x04034b50
    || header.readUInt16LE(6) !== expectedFlags
    || header.readUInt16LE(8) !== expectedMethod) {
    throw new Error('Update ZIP local header does not match its central directory.');
  }
  const nameLength = header.readUInt16LE(26);
  const extraLength = header.readUInt16LE(28);
  if (offset + 30 + nameLength + extraLength + compressedSize > centralOffset) {
    throw new Error('Update ZIP local entry overlaps metadata.');
  }
  const name = decodeUtf8(await readAt(handle, offset + 30, nameLength));
  if (name !== expectedName) throw new Error('Update ZIP local filename does not match its central directory.');
}

class StreamByteReader {
  private buffer = Buffer.alloc(0);
  private ended = false;

  constructor(
    private readonly iterator: AsyncIterator<Buffer | Uint8Array>,
    private readonly signal?: AbortSignal,
  ) {}

  async readExact(size: number, allowEnd = false): Promise<Buffer | undefined> {
    if (!Number.isSafeInteger(size) || size < 0) throw new Error('Invalid archive read size.');
    const chunks: Buffer[] = [];
    let collected = 0;
    while (collected < size) {
      throwIfAborted(this.signal);
      if (this.buffer.length === 0) {
        const next = await this.iterator.next();
        if (next.done) {
          this.ended = true;
          if (allowEnd && collected === 0) return undefined;
          throw new Error('Update archive is truncated.');
        }
        this.buffer = Buffer.from(next.value);
      }
      const take = Math.min(size - collected, this.buffer.length);
      chunks.push(this.buffer.subarray(0, take));
      this.buffer = this.buffer.subarray(take);
      collected += take;
    }
    return chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, size);
  }

  async skip(size: number): Promise<void> {
    let remaining = size;
    while (remaining > 0) {
      throwIfAborted(this.signal);
      if (this.buffer.length === 0) {
        const next = await this.iterator.next();
        if (next.done) {
          this.ended = true;
          throw new Error('Update archive is truncated.');
        }
        this.buffer = Buffer.from(next.value);
      }
      const take = Math.min(remaining, this.buffer.length);
      this.buffer = this.buffer.subarray(take);
      remaining -= take;
    }
  }
}

function validateArchivePath(value: string): string {
  if (!value || value.length > 640 || !/^[\x20-\x7e]+$/.test(value)
    || value.startsWith('/') || value.endsWith('/') || value.includes('\\') || value.includes(':')) {
    throw new Error(`Update archive contains an unsafe path: ${value}`);
  }
  const segments = value.split('/');
  if (segments.some((segment) => !segment || segment === '.' || segment === '..')) {
    throw new Error(`Update archive contains an unsafe path: ${value}`);
  }
  return value;
}

function validateTarChecksum(header: Buffer): void {
  const expected = parseTarNumber(header.subarray(148, 156));
  let actual = 0;
  for (let index = 0; index < header.length; index += 1) {
    actual += index >= 148 && index < 156 ? 0x20 : header[index];
  }
  if (actual !== expected) throw new Error('Update TAR header checksum is invalid.');
}

function parseTarNumber(bytes: Buffer): number {
  if ((bytes[0] & 0x80) !== 0) throw new Error('Base-256 TAR numbers are not supported.');
  const text = bytes.toString('ascii').replace(/\0.*$/, '').trim();
  if (!/^[0-7]+$/.test(text)) throw new Error('Update TAR contains an invalid numeric field.');
  const value = Number.parseInt(text, 8);
  if (!Number.isSafeInteger(value) || value < 0) throw new Error('Update TAR numeric field is out of range.');
  return value;
}

function parsePaxPath(bytes: Buffer): string {
  const text = decodeUtf8(bytes);
  let offset = 0;
  let pathValue: string | undefined;
  while (offset < text.length) {
    const space = text.indexOf(' ', offset);
    if (space < 0) throw new Error('Update TAR PAX metadata is malformed.');
    const length = Number(text.slice(offset, space));
    if (!Number.isSafeInteger(length) || length <= 0 || offset + length > text.length) {
      throw new Error('Update TAR PAX metadata is malformed.');
    }
    const record = text.slice(space + 1, offset + length);
    if (!record.endsWith('\n')) throw new Error('Update TAR PAX metadata is malformed.');
    const equals = record.indexOf('=');
    if (equals < 1) throw new Error('Update TAR PAX metadata is malformed.');
    const key = record.slice(0, equals);
    const value = record.slice(equals + 1, -1);
    if (key === 'path') pathValue = value;
    else if (key === 'linkpath' || key === 'size') throw new Error(`Update TAR PAX ${key} overrides are not supported.`);
    offset += length;
  }
  if (!pathValue) throw new Error('Update TAR PAX metadata has no path.');
  return pathValue;
}

function tarText(bytes: Buffer): string {
  return decodeUtf8(bytes.subarray(0, bytes.indexOf(0) >= 0 ? bytes.indexOf(0) : bytes.length));
}

function decodeUtf8(bytes: Uint8Array): string {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    throw new Error('Update archive contains an invalid UTF-8 path.');
  }
}

function paddedSize(size: number): number {
  return Math.ceil(size / blockSize) * blockSize;
}

function isZeroBlock(value: Buffer): boolean {
  return value.every((byte) => byte === 0);
}

async function readAt(handle: Awaited<ReturnType<typeof open>>, position: number, size: number): Promise<Buffer> {
  if (!Number.isSafeInteger(position) || !Number.isSafeInteger(size) || position < 0 || size < 0) {
    throw new Error('Update archive offset is invalid.');
  }
  const buffer = Buffer.allocUnsafe(size);
  const { bytesRead } = await handle.read(buffer, 0, size, position);
  if (bytesRead !== size) throw new Error('Update archive is truncated.');
  return buffer;
}

function findSignatureBackwards(buffer: Buffer, signature: number): number {
  for (let offset = buffer.length - 22; offset >= 0; offset -= 1) {
    if (buffer.readUInt32LE(offset) === signature) return offset;
  }
  return -1;
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw signal.reason ?? new Error('Update staging cancelled.');
}
