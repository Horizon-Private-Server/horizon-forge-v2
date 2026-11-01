import type { ProjectTransform, ProjectVector3 } from '../../types/EditorRuntime.js';
import type { OcclusionCoverageSnapshot, OcclusionCoverageState } from '../../types/OcclusionCoverage.js';
import { isOcclusionCoverageFingerprint, isOcclusionCoverageState, OCCLUSION_COVERAGE_LIMITS } from '../../utils/OcclusionCoverage.js';
import { BridgeErrorCode, BridgeProtocolError } from './BridgeProtocol.js';
import { PayloadReader, PayloadWriter } from './PayloadIO.js';

export function encodeOcclusionCoverageUpdate(fingerprint: string, state: OcclusionCoverageState): Buffer {
  if (!isOcclusionCoverageFingerprint(fingerprint) || !isOcclusionCoverageState(state)) malformed('Invalid occlusion coverage update.');
  const writer = new PayloadWriter();
  writer.writeString(fingerprint); writer.writeUInt32(state.schemaVersion); writer.writeBoolean(state.primaryEnabled);
  writer.writeUInt32(state.sources.length);
  for (const source of state.sources) {
    writer.writeString(source.id); writer.writeString(source.name); writer.writeBoolean(source.enabled);
    writer.writeUInt32(source.role === 'include' ? 1 : 2);
    vector(writer, source.transform.position);
    const q = source.transform.rotation;
    [q.x, q.y, q.z, q.w].forEach(v => writer.writeFloat32(v)); vector(writer, source.transform.scale);
    const attached = source.graph && (source.boxSize || source.cylinder);
    const sticky = source.graph && (source.graph.sticky !== undefined || source.graph.stickyDistance !== undefined);
    const kind = attached ? source.boxSize ? 5 : 6 : source.boxSize ? 1 : source.cylinder ? 2 : sticky ? 4 : 3;
    writer.writeUInt32(kind);
    if (source.boxSize) vector(writer, source.boxSize);
    else if (source.cylinder) { writer.writeFloat32(source.cylinder.radius); writer.writeFloat32(source.cylinder.height); }
    if (source.graph) {
      if (kind === 3 || kind === 4) vector(writer, source.graph.size!);
      if (attached) writer.writeBoolean(source.graph.disabled ?? false);
      if (kind !== 3) { writer.writeBoolean(source.graph.sticky ?? false); writer.writeFloat32(source.graph.stickyDistance ?? 12); }
      writer.writeUInt32(source.graph.markers.length);
      for (const marker of source.graph.markers) { writer.writeString(marker.id); writer.writeString(marker.name); vector(writer, marker.position); }
    }
  }
  return writer.toBuffer();
}

export function decodeOcclusionCoverageSnapshot(payload: Uint8Array): OcclusionCoverageSnapshot {
  const reader = new PayloadReader(payload);
  const projectFingerprint = reader.readString(), recipeFingerprint = reader.readString();
  const visibilityFingerprint = reader.readString();
  if (![projectFingerprint, recipeFingerprint, visibilityFingerprint].every(isOcclusionCoverageFingerprint)) malformed('Invalid coverage fingerprints.');
  const schemaVersion = reader.readUInt32();
  if (schemaVersion !== 1) malformed('Unsupported coverage schema.');
  const primaryEnabled = reader.readBoolean(), count = reader.readUInt32();
  if (count > OCCLUSION_COVERAGE_LIMITS.sources) malformed('Coverage source count exceeds limit.');
  const sources: OcclusionCoverageState['sources'] = [];
  let totalMarkers = 0;
  for (let i = 0; i < count; i++) {
    const id = reader.readString(), name = reader.readString(), enabled = reader.readBoolean(), role = reader.readUInt32();
    if (role !== 1 && role !== 2) malformed('Invalid coverage role.');
    const position = readVector(reader);
    const rotation = { x: reader.readFloat32(), y: reader.readFloat32(), z: reader.readFloat32(), w: reader.readFloat32() };
    const transform: ProjectTransform = { position, rotation, scale: readVector(reader) };
    const kind = reader.readUInt32();
    const source: OcclusionCoverageState['sources'][number] = { id, name, enabled, role: role === 1 ? 'include' : 'exclude', transform };
    if (kind === 1 || kind === 5) source.boxSize = readVector(reader);
    else if (kind === 2 || kind === 6) source.cylinder = { radius: reader.readFloat32(), height: reader.readFloat32() };
    else if (kind !== 3 && kind !== 4) malformed('Invalid coverage source kind.');
    if (kind >= 3 && kind <= 6) {
      const size = kind === 3 || kind === 4 ? { size: readVector(reader) } : {};
      const disabled = kind >= 5 && reader.readBoolean();
      const sticky = kind !== 3 ? { sticky: reader.readBoolean(), stickyDistance: reader.readFloat32() } : {};
      const markerCount = reader.readUInt32();
      totalMarkers += markerCount;
      if (markerCount > OCCLUSION_COVERAGE_LIMITS.markersPerGraph || totalMarkers > OCCLUSION_COVERAGE_LIMITS.markers) malformed('Coverage marker count exceeds limit.');
      const markers = Array.from({ length: markerCount }, () => ({ id: reader.readString(), name: reader.readString(), position: readVector(reader) }));
      source.graph = { ...size, markers, ...sticky, ...(disabled ? { disabled: true } : {}) };
    }
    sources.push(source);
  }
  reader.complete();
  const state = { schemaVersion, primaryEnabled, sources };
  if (!isOcclusionCoverageState(state)) malformed('Invalid occlusion coverage state.');
  return { projectFingerprint, recipeFingerprint, visibilityFingerprint, state };
}

function vector(writer: PayloadWriter, v: ProjectVector3): void { writer.writeFloat32(v.x); writer.writeFloat32(v.y); writer.writeFloat32(v.z); }
function readVector(reader: PayloadReader): ProjectVector3 { return { x: reader.readFloat32(), y: reader.readFloat32(), z: reader.readFloat32() }; }
function malformed(message: string): never { throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, message); }
