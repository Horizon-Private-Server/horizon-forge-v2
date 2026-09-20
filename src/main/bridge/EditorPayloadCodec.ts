import type {
  EditorCommand,
  EditorEntity,
  EditorEvent,
  EditorSnapshot,
  ProjectTransform,
  ProjectVector3,
} from '../../types/EditorRuntime.js';
import { PayloadReader, PayloadWriter, malformed } from './PayloadIO.js';

const MAX_ENTITIES = 100_000;
const MAX_EVENTS = 1_024;
const commandKinds = { setSelection: 1, renameProject: 2, updateTransform: 3 } as const;
const eventKinds: Record<number, EditorEvent['kind']> = {
  1: 'projectOpened', 2: 'projectChanged', 3: 'selectionChanged', 4: 'projectSaved',
  5: 'recoveryWritten', 6: 'diagnosticRaised', 7: 'projectClosed',
};
const severities: Record<number, EditorSnapshot['diagnostics'][number]['severity']> = {
  1: 'info', 2: 'warning', 3: 'error',
};

export function encodeEditorOpenRequest(projectPath: string, autosaveSeconds: number): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(projectPath);
  writer.writeUInt32(autosaveSeconds);
  return writer.toBuffer();
}

export function encodeEditorCommand(value: EditorCommand): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.id);
  writer.writeUInt32(commandKinds[value.kind]);
  writeStrings(writer, value.entityIds);
  writer.writeBoolean(value.kind === 'updateTransform');
  if (value.kind === 'updateTransform') writeTransform(writer, value.transform);
  writer.writeBoolean(value.kind === 'renameProject');
  if (value.kind === 'renameProject') writer.writeString(value.text);
  return writer.toBuffer();
}

export function encodeEditorEventRequest(afterSequence: number, limit: number): Buffer {
  const writer = new PayloadWriter();
  writer.writeUInt64(afterSequence);
  writer.writeUInt32(limit);
  return writer.toBuffer();
}

export function decodeEditorSnapshot(payload: Uint8Array): EditorSnapshot {
  const reader = new PayloadReader(payload);
  const projectPath = reader.readString();
  const projectId = reader.readString();
  const projectName = reader.readString();
  const target = {
    game: reader.readString(), region: reader.readString(),
    revision: reader.readString(), bakeProfile: reader.readString(),
  };
  const baseLevel = {
    game: reader.readString(), region: reader.readString(), revision: reader.readString(),
    level: reader.readUInt32(), sourceFingerprint: reader.readString(), missingAssetCount: reader.readUInt32(),
  };
  const entities = readList(reader, MAX_ENTITIES, () => readEntity(reader));
  const selection = readStrings(reader, MAX_ENTITIES);
  const isDirty = reader.readBoolean();
  const migrationPending = reader.readBoolean();
  const lastEventSequence = reader.readUInt64();
  const capabilities = reader.readStrings();
  const tools = readList(reader, 64, () => ({
    id: reader.readString(), label: reader.readString(), capability: reader.readString(),
  }));
  const diagnostics = readList(reader, 100, () => ({
    code: reader.readString(), severity: enumValue(severities, reader.readUInt32(), 'diagnostic severity'),
    message: reader.readString(),
  }));
  reader.complete();
  return {
    projectPath, projectId, projectName, target, baseLevel, entities, selection, isDirty,
    migrationPending, lastEventSequence, capabilities, tools, diagnostics,
  };
}

export function decodeEditorEvents(payload: Uint8Array): EditorEvent[] {
  const reader = new PayloadReader(payload);
  const values = readList(reader, MAX_EVENTS, () => ({
    sequence: reader.readUInt64(),
    createdUnixMilliseconds: reader.readUInt64(),
    kind: enumValue(eventKinds, reader.readUInt32(), 'event kind'),
    commandId: reader.readBoolean() ? reader.readString() : undefined,
    entityIds: readStrings(reader, MAX_ENTITIES),
    message: reader.readBoolean() ? reader.readString() : undefined,
  }));
  reader.complete();
  return values;
}

function readEntity(reader: PayloadReader): EditorEntity {
  const value: EditorEntity = {
    id: reader.readString(), name: reader.readString(), layer: reader.readString(), transform: readTransform(reader),
  };
  if (reader.readBoolean()) value.asset = { id: reader.readString(), kind: reader.readString() };
  if (reader.readBoolean()) value.provenance = {
    game: reader.readString(), level: reader.readUInt32(), section: reader.readString(), sourceIndex: reader.readUInt32(),
  };
  return value;
}

function writeTransform(writer: PayloadWriter, value: ProjectTransform): void {
  writeVector(writer, value.position);
  writer.writeFloat32(value.rotation.x);
  writer.writeFloat32(value.rotation.y);
  writer.writeFloat32(value.rotation.z);
  writer.writeFloat32(value.rotation.w);
  writeVector(writer, value.scale);
}

function readTransform(reader: PayloadReader): ProjectTransform {
  return {
    position: readVector(reader),
    rotation: { ...readVector(reader), w: reader.readFloat32() },
    scale: readVector(reader),
  };
}

function writeVector(writer: PayloadWriter, value: ProjectVector3): void {
  writer.writeFloat32(value.x);
  writer.writeFloat32(value.y);
  writer.writeFloat32(value.z);
}

function readVector(reader: PayloadReader): ProjectVector3 {
  return { x: reader.readFloat32(), y: reader.readFloat32(), z: reader.readFloat32() };
}

function writeStrings(writer: PayloadWriter, values: string[]): void {
  if (values.length > MAX_ENTITIES) malformed('Entity ID list exceeds item limit');
  writer.writeUInt32(values.length);
  values.forEach((value) => writer.writeString(value));
}

function readStrings(reader: PayloadReader, maximum: number): string[] {
  return readList(reader, maximum, () => reader.readString());
}

function readList<T>(reader: PayloadReader, maximum: number, read: () => T): T[] {
  const count = reader.readUInt32();
  if (count > maximum) malformed('List exceeds item limit');
  return Array.from({ length: count }, read);
}

function enumValue<T extends string>(values: Record<number, T>, index: number, name: string): T {
  const value = values[index];
  if (!value) malformed(`Unknown editor ${name}`);
  return value;
}
