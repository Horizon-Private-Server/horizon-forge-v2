import type {
  EditorCommand,
  EditorEntity,
  EditorEvent,
  EditorLevelSettings,
  EditorSnapshot,
  EditorTieCollisionGenerationSettings,
  EditorTieCollisionPreview,
  EditorTieCollisionSourceInfo,
  EditorTieCollisionRecipe,
  ProjectTransform,
  ProjectVector3,
} from '../../types/EditorRuntime.js';
import { PayloadReader, PayloadWriter, malformed } from './PayloadIO.js';

const MAX_ENTITIES = 100_000;
const MAX_EVENTS = 1_024;
const MAX_OCTANTS = 1_000_000;
const commandKinds = {
  setSelection: 1,
  renameProject: 2,
  updateTransform: 3,
  renameEntity: 4,
  setEntityLayer: 5,
  setEntityState: 6,
  updateTransforms: 7,
  undo: 8,
  redo: 9,
  deleteEntities: 10,
  duplicateEntities: 11,
  copyEntities: 12,
  pasteEntities: 13,
  updateLevelSettings: 14,
  updateSplinePoints: 15,
  createEntityFromAsset: 16,
  addSkyShellFromAsset: 17,
  updateSkyShell: 18,
  reorderSkyShell: 19,
  removeTieCollisionProxy: 20,
  setTieCollisionEnabled: 21,
  setTieCollisionRawType: 22,
} as const;
const eventKinds: Record<number, EditorEvent['kind']> = {
  1: 'projectOpened', 2: 'projectChanged', 3: 'selectionChanged', 4: 'projectSaved',
  5: 'recoveryWritten', 6: 'diagnosticRaised', 7: 'projectClosed',
};
const severities: Record<number, EditorSnapshot['diagnostics'][number]['severity']> = {
  1: 'info', 2: 'warning', 3: 'error',
};
const geometryKinds: Record<number, NonNullable<EditorEntity['geometry']>['kind']> = {
  1: 'cuboid', 2: 'spline', 3: 'area', 4: 'sphere', 5: 'cylinder', 6: 'pill', 7: 'grindPath',
  8: 'directionalLight', 9: 'pointLight', 10: 'environmentSample', 11: 'environmentTransition',
  12: 'camera', 13: 'ambientSound',
};

export function encodeEditorOpenRequest(projectPath: string, catalogRootPath: string, autosaveSeconds: number): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(projectPath);
  writer.writeString(catalogRootPath);
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
  const transforms = value.kind === 'updateTransforms' ? value.transforms : [];
  writer.writeUInt32(transforms.length);
  transforms.forEach((update) => {
    writer.writeString(update.entityId);
    writeTransform(writer, update.transform);
  });
  const hasText = value.kind === 'renameProject' || value.kind === 'renameEntity' || value.kind === 'setEntityLayer';
  writer.writeBoolean(hasText);
  if (hasText) writer.writeString(value.text);
  writer.writeBoolean(value.kind === 'setEntityState');
  if (value.kind === 'setEntityState') {
    writeOptionalBoolean(writer, value.state.hidden);
    writeOptionalBoolean(writer, value.state.disabled);
    writeOptionalBoolean(writer, value.state.locked);
  }
  writer.writeBoolean(value.kind === 'updateLevelSettings');
  if (value.kind === 'updateLevelSettings') writeLevelSettings(writer, value.levelSettings);
  writer.writeBoolean(value.kind === 'updateSplinePoints');
  if (value.kind === 'updateSplinePoints') {
    writer.writeUInt32(value.points.length);
    value.points.forEach((point) => {
      writer.writeFloat32(point.x);
      writer.writeFloat32(point.y);
      writer.writeFloat32(point.z);
      writer.writeFloat32(point.w);
    });
  }
  writer.writeBoolean(value.kind === 'createEntityFromAsset');
  if (value.kind === 'createEntityFromAsset') {
    writer.writeString(value.placement.assetId);
    writer.writeString(value.placement.kind);
    writer.writeUInt32(value.placement.classId);
    writeTransform(writer, value.placement.transform);
  }
  writer.writeBoolean(value.kind === 'addSkyShellFromAsset');
  if (value.kind === 'addSkyShellFromAsset') {
    writer.writeString(value.source.assetId);
    writer.writeUInt32(value.source.shellIndex);
  }
  writer.writeBoolean(value.kind === 'updateSkyShell');
  if (value.kind === 'updateSkyShell') {
    writer.writeBoolean(value.update.initialRotationRadians !== undefined);
    if (value.update.initialRotationRadians) writeVector(writer, value.update.initialRotationRadians);
    writer.writeBoolean(value.update.angularVelocityRadiansPerSecond !== undefined);
    if (value.update.angularVelocityRadiansPerSecond)
      writeVector(writer, value.update.angularVelocityRadiansPerSecond);
  }
  writer.writeBoolean(value.kind === 'reorderSkyShell');
  if (value.kind === 'reorderSkyShell') writer.writeUInt32(value.destinationOrder);
  writer.writeBoolean(value.kind === 'setTieCollisionEnabled');
  if (value.kind === 'setTieCollisionEnabled') writer.writeBoolean(value.enabled);
  writer.writeBoolean(value.kind === 'setTieCollisionRawType');
  if (value.kind === 'setTieCollisionRawType') writer.writeUInt32(value.rawType);
  return writer.toBuffer();
}

export function encodeEditorEventRequest(afterSequence: number, limit: number): Buffer {
  const writer = new PayloadWriter();
  writer.writeUInt64(afterSequence);
  writer.writeUInt32(limit);
  return writer.toBuffer();
}

export function encodeTieCollisionPreviewRequest(
  entityId: string,
  settings?: EditorTieCollisionGenerationSettings,
): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(entityId);
  writer.writeBoolean(settings !== undefined);
  if (settings) {
    writer.writeUInt32(settings.rawType);
    writer.writeUInt32(settings.profileSections);
    writer.writeUInt32(settings.surfaceLodIndex + 1);
    writer.writeBoolean(settings.useHull);
  }
  return writer.toBuffer();
}

export function encodeTieCollisionSourceRequest(entityId: string): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(entityId);
  return writer.toBuffer();
}

export function decodeTieCollisionSourceInfo(payload: Uint8Array): EditorTieCollisionSourceInfo {
  const reader = new PayloadReader(payload);
  const tieAssetId = reader.readString();
  const surfaceLodIndices = readList(reader, 3, () => reader.readUInt32());
  reader.complete();
  if (surfaceLodIndices.some((value) => value > 2 || !Number.isInteger(value)))
    malformed('Invalid TIE surface LOD index');
  return { tieAssetId, surfaceLodIndices };
}

export function encodeTieCollisionApplyRequest(commandId: string, token: string): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(commandId);
  writer.writeString(token);
  return writer.toBuffer();
}

export function encodeTieCollisionRenderRequest(
  cacheRootPath: string,
  catalogRootPath: string,
  token: string,
): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(cacheRootPath);
  writer.writeString(catalogRootPath);
  writer.writeString(token);
  return writer.toBuffer();
}

export function decodeTieCollisionPreview(payload: Uint8Array): EditorTieCollisionPreview {
  const reader = new PayloadReader(payload);
  const tieAssetId = reader.readString();
  const candidates = readList(reader, 16, () => ({
    token: reader.readString(),
    preset: enumValue(
      { 1: 'surface', 3: 'solidHull' },
      reader.readUInt32(),
      'TIE collision preset',
    ),
    label: reader.readString(),
    recipe: readTieCollisionRecipe(reader),
    encodedByteCount: reader.readUInt32(),
    vertexCount: reader.readUInt32(),
    faceCount: reader.readUInt32(),
    occupiedOctantCount: reader.readUInt32(),
    duplicateFaceCount: reader.readUInt32(),
    hardViolationCount: reader.readUInt32(),
    maximumDeviation: reader.readFloat32(),
    deviationSampleCount: reader.readUInt32(),
    octants: readCollisionOctants(reader),
    combinedAnalysis: reader.readBoolean() ? {
      instanceCount: reader.readUInt32(),
      logicalFaceCount: reader.readUInt32(),
      occupiedOctantCount: reader.readUInt32(),
      duplicateFaceCount: reader.readUInt32(),
      hardViolationCount: reader.readUInt32(),
      octants: readCollisionOctants(reader),
      error: reader.readBoolean() ? reader.readString() : undefined,
    } : undefined,
  }));
  reader.complete();
  return { tieAssetId, candidates };
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
  const levelSettings = reader.readBoolean() ? readLevelSettings(reader) : undefined;
  const entities = readList(reader, MAX_ENTITIES, () => readEntity(reader));
  const selection = readStrings(reader, MAX_ENTITIES);
  const isDirty = reader.readBoolean();
  const migrationPending = reader.readBoolean();
  const canUndo = reader.readBoolean();
  const canRedo = reader.readBoolean();
  const canPaste = reader.readBoolean();
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
    projectPath, projectId, projectName, target, baseLevel, levelSettings, entities, selection, isDirty,
    migrationPending, canUndo, canRedo, canPaste, lastEventSequence, capabilities, tools, diagnostics,
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
    transformModes: [],
    state: {
      dirty: false, hidden: false, disabled: false, locked: false, readOnly: false,
      invalid: false, missingAsset: false,
    },
  };
  if (reader.readBoolean()) value.asset = { id: reader.readString(), kind: reader.readString() };
  if (reader.readBoolean()) value.provenance = {
    game: reader.readString(), level: reader.readUInt32(), section: reader.readString(), sourceIndex: reader.readUInt32(),
  };
  if (reader.readBoolean()) value.sourceClassId = reader.readUInt32();
  if (reader.readBoolean()) value.geometry = {
    kind: enumValue(geometryKinds, reader.readUInt32(), 'geometry kind'),
    points: readList(reader, MAX_ENTITIES, () => ({
      x: reader.readFloat32(), y: reader.readFloat32(), z: reader.readFloat32(), w: reader.readFloat32(),
    })),
  };
  if (reader.readBoolean()) value.skyShell = {
    sourceShellIndex: reader.readUInt32(),
    order: reader.readUInt32(),
    initialRotationRadians: readVector(reader),
    angularVelocityRadiansPerSecond: readVector(reader),
  };
  if (reader.readBoolean()) value.collision = {
    kind: enumValue({ 1: 'solid', 2: 'playerBarrier' }, reader.readUInt32(), 'collision piece kind'),
    sourcePayloadIndex: reader.readUInt32(),
    sourcePieceIndex: reader.readUInt32(),
    faceCount: reader.readUInt32(),
    vertexCount: reader.readUInt32(),
    types: readList(reader, 256, () => ({ rawType: reader.readUInt32(), count: reader.readUInt32() })),
  };
  if (reader.readBoolean()) value.tieCollision = {
    proxyAssetId: reader.readString(),
    recipe: readTieCollisionRecipe(reader),
  };
  if (reader.readBoolean()) value.tieCollisionEnabled = reader.readBoolean();
  const transformCapabilities = reader.readUInt32();
  value.transformModes = [
    transformCapabilities & 1 ? 'translate' : undefined,
    transformCapabilities & 2 ? 'rotate' : undefined,
    transformCapabilities & 4 ? 'scale' : undefined,
  ].filter((mode): mode is EditorEntity['transformModes'][number] => mode !== undefined);
  value.state = {
    dirty: reader.readBoolean(),
    hidden: reader.readBoolean(),
    disabled: reader.readBoolean(),
    locked: reader.readBoolean(),
    readOnly: reader.readBoolean(),
    invalid: reader.readBoolean(),
    missingAsset: reader.readBoolean(),
  };
  return value;
}

function readTieCollisionRecipe(reader: PayloadReader): EditorTieCollisionRecipe {
  return {
    kind: enumValue({ 0: 'surface', 1: 'wrap', 2: 'hull' }, reader.readUInt32(), 'TIE collision recipe kind'),
    generatorVersion: reader.readUInt32(),
    recipeVersion: reader.readUInt32(),
    lodIndex: reader.readUInt32(),
    rawType: reader.readUInt32(),
    detailSize: reader.readFloat32(),
    sealOpeningSize: reader.readFloat32(),
    surfaceOffset: reader.readFloat32(),
    openBase: reader.readBoolean(),
    profileSections: reader.readUInt32(),
  };
}

function readCollisionOctants(reader: PayloadReader) {
  return readList(reader, MAX_OCTANTS, () => ({
    x: reader.readUInt32() | 0,
    y: reader.readUInt32() | 0,
    z: reader.readUInt32() | 0,
    faceCount: reader.readUInt32(),
    vertexCount: reader.readUInt32(),
    quadCount: reader.readUInt32(),
    encodedByteCount: reader.readUInt32(),
    violations: reader.readStrings(),
    additionIds: reader.readStrings(),
  }));
}

function writeOptionalBoolean(writer: PayloadWriter, value: boolean | undefined): void {
  writer.writeBoolean(value !== undefined);
  if (value !== undefined) writer.writeBoolean(value);
}

function writeLevelSettings(writer: PayloadWriter, value: EditorLevelSettings): void {
  value.backgroundColor.forEach((channel) => writer.writeUInt32(channel));
  value.fogColor.forEach((channel) => writer.writeUInt32(channel));
  writer.writeFloat32(value.fogNearDistance);
  writer.writeFloat32(value.fogFarDistance);
  writer.writeFloat32(value.fogNearIntensity);
  writer.writeFloat32(value.fogFarIntensity);
}

function readLevelSettings(reader: PayloadReader): EditorLevelSettings {
  return {
    backgroundColor: [reader.readUInt32(), reader.readUInt32(), reader.readUInt32()],
    fogColor: [reader.readUInt32(), reader.readUInt32(), reader.readUInt32()],
    fogNearDistance: reader.readFloat32(),
    fogFarDistance: reader.readFloat32(),
    fogNearIntensity: reader.readFloat32(),
    fogFarIntensity: reader.readFloat32(),
  };
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
