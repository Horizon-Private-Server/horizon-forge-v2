import type { OcclusionCoveragePreview } from '../../types/OcclusionCoverage.js';
import { isOcclusionCoverageFingerprint } from '../../utils/OcclusionCoverage.js';
import { BridgeErrorCode, BridgeProtocolError } from './BridgeProtocol.js';
import { PayloadReader, PayloadWriter } from './PayloadIO.js';

export function encodeOcclusionCoveragePreviewRequest(fingerprint: string, sourcePath: string): Buffer {
  if (!isOcclusionCoverageFingerprint(fingerprint) || sourcePath.length > 4096 || sourcePath.includes('\0')) malformed('Invalid coverage preview request.');
  const writer = new PayloadWriter(); writer.writeString(fingerprint); writer.writeString(sourcePath); return writer.toBuffer();
}

export function decodeOcclusionCoveragePreview(bytes: Uint8Array): OcclusionCoveragePreview {
  const reader = new PayloadReader(bytes);
  const fingerprint = () => {
    const value = reader.readString();
    if (!isOcclusionCoverageFingerprint(value)) malformed('Invalid coverage preview fingerprint.');
    return value;
  };
  const bounded = (maximum: number) => {
    const value = reader.readUInt32(); if (value > maximum) malformed('Coverage preview exceeds its limit.'); return value;
  };
  const projectFingerprint = fingerprint(), recipeFingerprint = fingerprint();
  const algorithmVersion = bounded(10_000), cellSize = reader.readFloat32();
  if (!algorithmVersion || !Number.isFinite(cellSize) || cellSize <= 0 || cellSize > 1_000_000) malformed('Invalid coverage preview profile.');
  const primaryFingerprint = fingerprint(), coordinateFingerprint = fingerprint();
  const primaryCells = bounded(100_000), candidateTests = bounded(2_000_000), sourceCount = bounded(1024);
  const ids = new Set<string>(), sources: OcclusionCoveragePreview['sources'] = [];
  for (let i = 0; i < sourceCount; i++) {
    const id = reader.readString();
    if (!/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(id) || id === '00000000-0000-0000-0000-000000000000' || ids.has(id)) malformed('Invalid coverage preview source.');
    ids.add(id); sources.push({ id, cells: bounded(100_000) });
  }
  const readCells = (maximum: number): OcclusionCoveragePreview['cells'] => {
    const count = bounded(maximum), values: OcclusionCoveragePreview['cells'] = [];
    for (let i = 0; i < count; i++) {
      const cell = { x: bounded(0x7fff_ffff), y: bounded(0x7fff_ffff), z: bounded(0x7fff_ffff) };
      const previous = values[i - 1];
      if (previous && (previous.z > cell.z || previous.z === cell.z && (previous.y > cell.y || previous.y === cell.y && previous.x >= cell.x))) malformed('Coverage preview cells must be unique and in Z/Y/X order.');
      values.push(cell);
    }
    return values;
  };
  const cells = readCells(100_000);
  if (candidateTests < primaryCells || candidateTests < cells.length) malformed('Invalid coverage preview counts.');
  let graphPreview: OcclusionCoveragePreview['graphPreview'];
  if (reader.remainingBytes) {
    if (algorithmVersion !== 3 || reader.readUInt32() !== 1) malformed('Unsupported graph preview extension.');
    const sceneFingerprint = fingerprint(), rays = bounded(2_000_000), queryTests = bounded(200_000_000), count = bounded(1024);
    if (!count || !rays) malformed('Invalid graph preview counts.');
    const graphs: NonNullable<OcclusionCoveragePreview['graphPreview']>['sources'] = [], graphIds = new Set<string>();
    let remaining = 100_000;
    for (let i = 0; i < count; i++) {
      const id = reader.readString();
      if (!ids.has(id) || graphIds.has(id)) malformed('Invalid graph preview ownership.');
      graphIds.add(id);
      const accepted = readCells(remaining); remaining -= accepted.length;
      const rejected = readCells(remaining); remaining -= rejected.length;
      if (sources.find(s => s.id === id)!.cells !== accepted.length) malformed('Graph contribution counts do not match accepted cells.');
      const keys = new Set(accepted.map(c => `${c.x}:${c.y}:${c.z}`));
      if (rejected.some(c => keys.has(`${c.x}:${c.y}:${c.z}`))) malformed('Graph accepted and rejected cells overlap.');
      graphs.push({ id, accepted, rejected });
    }
    if (candidateTests < 100_000 - remaining) malformed('Invalid graph candidate counts.');
    graphPreview = { sceneFingerprint, rays, queryTests, sources: graphs };
  } else if (algorithmVersion === 3) malformed('Graph preview scene identity is missing.');
  reader.complete();
  return { projectFingerprint, recipeFingerprint, algorithmVersion, cellSize, primaryFingerprint, coordinateFingerprint,
    primaryCells, candidateTests, sources, cells, ...(graphPreview ? { graphPreview } : {}) };
}

function malformed(message: string): never { throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, message); }
