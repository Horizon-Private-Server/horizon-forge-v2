import type {
  EditorCommand,
  EditorEntity,
  EditorEvent,
  EditorLevelSettings,
  EditorSnapshot,
  EditorInstancedCollisionGenerationSettings,
  EditorInstancedCollisionPreview,
  EditorInstancedCollisionSourceInfo,
  EditorInstancedCollisionRecipe,
  ProjectTransform,
  ProjectVector3,
} from '../../types/EditorRuntime.js';
import { PayloadReader, PayloadWriter, malformed } from './PayloadIO.js';

const MAX_ENTITIES = 100_000;
const MAX_REFERENCES = 1_000_000;
const MAX_EVENTS = 1_024;
const MAX_OCTANTS = 1_000_000;
const MAX_HUD_IMAGE_BYTES = 16 * 1024 * 1024;
const MAX_FX_IMAGE_BYTES = 16 * 1024 * 1024;
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
  removeInstancedCollisionProxy: 20,
  setInstancedCollisionEnabled: 21,
  setInstancedCollisionRawType: 22,
  setInstancedCollisionFaceTypes: 23,
  setEntityReference: 24,
  updatePaletteOptimization: 25,
  replaceHudTexture: 26,
  removeHudTextureOverride: 27,
  addHudIcon: 28,
  removeHudIcon: 29,
  replaceFxTexture: 30,
  removeFxTextureOverride: 31,
  addFxTexture: 32,
  removeFxTexture: 33,
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
const entityKinds = {
  1: 'entity', 2: 'moby', 3: 'tie', 4: 'shrub', 5: 'tfrag', 6: 'cuboid', 7: 'sphere',
  8: 'cylinder', 9: 'pill', 10: 'spline', 11: 'grindPath', 12: 'area', 13: 'collision',
  14: 'skyShell', 15: 'directionalLight', 16: 'pointLight', 17: 'environmentSample',
  18: 'environmentTransition', 19: 'camera', 20: 'ambientSound',
} as const;

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
  writer.writeBoolean(value.kind === 'setInstancedCollisionEnabled' && value.enabled !== null);
  if (value.kind === 'setInstancedCollisionEnabled' && value.enabled !== null) writer.writeBoolean(value.enabled);
  writer.writeBoolean(value.kind === 'setInstancedCollisionRawType');
  if (value.kind === 'setInstancedCollisionRawType') writer.writeUInt32(value.rawType);
  writer.writeBoolean(value.kind === 'setInstancedCollisionFaceTypes');
  if (value.kind === 'setInstancedCollisionFaceTypes') {
    writer.writeString(value.expectedProxyAssetId);
    writer.writeUInt32(value.faceTypes.length);
    value.faceTypes.forEach((faceType) => {
      writer.writeUInt32(faceType.faceIndex);
      writer.writeUInt32(faceType.rawType);
    });
  }
  writer.writeBoolean(value.kind === 'setEntityReference');
  if (value.kind === 'setEntityReference') {
    writer.writeString(value.reference.fieldKey);
    writer.writeBoolean(value.reference.sourceValue !== undefined);
    if (value.reference.sourceValue !== undefined) writer.writeUInt32(value.reference.sourceValue);
    writer.writeBoolean(value.reference.targetEntityId !== undefined);
    if (value.reference.targetEntityId !== undefined) writer.writeString(value.reference.targetEntityId);
  }
  writer.writeBoolean(value.kind === 'updatePaletteOptimization');
  if (value.kind === 'updatePaletteOptimization') {
    writer.writeString(value.paletteOptimization.mappingVersion);
    writer.writeUInt32(value.paletteOptimization.strength);
  }
  const isHud = value.kind === 'replaceHudTexture' || value.kind === 'removeHudTextureOverride'
    || value.kind === 'addHudIcon' || value.kind === 'removeHudIcon';
  writer.writeBoolean(isHud);
  if (isHud) {
    const hasSource = value.kind === 'replaceHudTexture' || value.kind === 'removeHudTextureOverride';
    writer.writeBoolean(hasSource);
    if (hasSource) writer.writeString(value.sourceAssetId);
    const hasSprite = value.kind === 'addHudIcon' || value.kind === 'removeHudIcon';
    writer.writeBoolean(hasSprite);
    if (hasSprite) writer.writeUInt32(value.spriteId);
    writer.writeBoolean(value.kind === 'addHudIcon');
    if (value.kind === 'addHudIcon') writer.writeUInt32(value.bankIndex);
    const hasImage = value.kind === 'replaceHudTexture' || value.kind === 'addHudIcon';
    writer.writeBoolean(hasImage);
    if (hasImage) writer.writeString(value.imageFormat);
    writer.writeBoolean(hasImage);
    if (hasImage) writer.writeBytes(value.imageBytes, MAX_HUD_IMAGE_BYTES);
  }
  const isFx = value.kind === 'replaceFxTexture' || value.kind === 'removeFxTextureOverride'
    || value.kind === 'addFxTexture' || value.kind === 'removeFxTexture';
  writer.writeBoolean(isFx);
  if (isFx) {
    const hasSource = value.kind === 'replaceFxTexture' || value.kind === 'removeFxTextureOverride';
    writer.writeBoolean(hasSource);
    if (hasSource) writer.writeString(value.sourceAssetId);
    writer.writeBoolean(value.kind === 'removeFxTexture');
    if (value.kind === 'removeFxTexture') writer.writeUInt32(value.index);
    const hasImage = value.kind === 'replaceFxTexture' || value.kind === 'addFxTexture';
    writer.writeBoolean(hasImage);
    if (hasImage) writer.writeString(value.imageFormat);
    writer.writeBoolean(hasImage);
    if (hasImage) writer.writeBytes(value.imageBytes, MAX_FX_IMAGE_BYTES);
  }
  return writer.toBuffer();
}

export function encodeEditorEventRequest(afterSequence: number, limit: number): Buffer {
  const writer = new PayloadWriter();
  writer.writeUInt64(afterSequence);
  writer.writeUInt32(limit);
  return writer.toBuffer();
}

export function encodeInstancedCollisionPreviewRequest(
  entityId: string,
  settings?: EditorInstancedCollisionGenerationSettings,
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

export function encodeInstancedCollisionSourceRequest(entityId: string): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(entityId);
  return writer.toBuffer();
}

export function decodeInstancedCollisionSourceInfo(payload: Uint8Array): EditorInstancedCollisionSourceInfo {
  const reader = new PayloadReader(payload);
  const sourceAssetId = reader.readString();
  const surfaceLodIndices = readList(reader, 3, () => reader.readUInt32());
  reader.complete();
  if (surfaceLodIndices.some((value) => value > 2 || !Number.isInteger(value)))
    malformed('Invalid TIE surface LOD index');
  return { sourceAssetId, surfaceLodIndices };
}

export function encodeInstancedCollisionApplyRequest(commandId: string, token: string): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(commandId);
  writer.writeString(token);
  return writer.toBuffer();
}

export function encodeInstancedCollisionRenderRequest(
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

export function decodeInstancedCollisionPreview(payload: Uint8Array): EditorInstancedCollisionPreview {
  const reader = new PayloadReader(payload);
  const sourceAssetId = reader.readString();
  const candidates = readList(reader, 16, () => ({
    token: reader.readString(),
    preset: enumValue(
      { 1: 'surface', 3: 'solidHull' },
      reader.readUInt32(),
      'instanced collision preset',
    ),
    label: reader.readString(),
    recipe: readInstancedCollisionRecipe(reader),
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
  return { sourceAssetId, candidates };
}

export function decodeEditorSnapshot(payload: Uint8Array): EditorSnapshot {
  const reader = new PayloadReader(payload);
  const projectPath = reader.readString();
  const projectId = reader.readString();
  const projectName = reader.readString();
  const targetGame = reader.readString();
  const targetRegion = reader.readString();
  const targetRevision = reader.readString();
  const bakeProfile = reader.readString();
  const mappingVersion = reader.readString();
  if (mappingVersion !== 'paletteOptimization.v1') malformed('Unsupported palette optimization mapping');
  const paletteStrength = reader.readUInt32();
  if (paletteStrength > 100) malformed('Invalid palette optimization strength');
  const target = {
    game: targetGame, region: targetRegion, revision: targetRevision, bakeProfile,
    paletteOptimization: { mappingVersion: 'paletteOptimization.v1' as const, strength: paletteStrength },
  };
  const baseLevel = {
    game: reader.readString(), region: reader.readString(), revision: reader.readString(),
    level: reader.readUInt32(), sourceFingerprint: reader.readString(), missingAssetCount: reader.readUInt32(),
  };
  const levelSettings = reader.readBoolean() ? readLevelSettings(reader) : undefined;
  const hud = reader.readBoolean() ? readHud(reader) : undefined;
  const fx = reader.readBoolean() ? readFx(reader) : undefined;
  const entities = readList(reader, MAX_ENTITIES, () => readEntity(reader));
  const references = readList(reader, MAX_REFERENCES, () => readReference(reader));
  const selection = readStrings(reader, MAX_ENTITIES);
  const isDirty = reader.readBoolean();
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
    projectPath, projectId, projectName, target, baseLevel, levelSettings, hud, fx, entities, references, selection, isDirty,
    canUndo, canRedo, canPaste, lastEventSequence, capabilities, tools, diagnostics,
  };
}

function readFx(reader: PayloadReader): NonNullable<EditorSnapshot['fx']> {
  const canRead = reader.readBoolean();
  const isDirty = reader.readBoolean();
  const canReplace = reader.readBoolean();
  const canAppend = reader.readBoolean();
  const authoringDisabledReason = optionalString(reader.readString());
  const maximumTextureCount = reader.readUInt32();
  const sourceTextures = readList(reader, 4_096, () => ({
    sourceIndex: reader.readUInt32(),
    label: reader.readString(),
    width: reader.readInt32(),
    height: reader.readInt32(),
    paletteOffset: reader.readInt32(),
    pixelOffset: reader.readInt32(),
    isSwizzled: reader.readBoolean(),
    sourceTexture: readTextureReference(reader),
    effectiveTexture: readTextureReference(reader),
    diagnostic: optionalString(reader.readString()),
  }));
  const additions = readList(reader, 4_096, () => ({
    width: reader.readUInt32(),
    height: reader.readUInt32(),
    texture: readTextureReference(reader) ?? malformed('FX addition texture is missing'),
  }));
  return {
    canRead, isDirty, canReplace, canAppend, authoringDisabledReason,
    maximumTextureCount, sourceTextures, additions,
  };
}

function readHud(reader: PayloadReader): NonNullable<EditorSnapshot['hud']> {
  const canRead = reader.readBoolean();
  const isDirty = reader.readBoolean();
  const canReplace = reader.readBoolean();
  const canAppend = reader.readBoolean();
  const disabledReason = reader.readString();
  const physicalBankCount = reader.readUInt32();
  const minimumAppendBank = reader.readUInt32();
  const minimumAppendSpriteId = reader.readUInt32();
  const maximumAppendSpriteId = reader.readUInt32();
  const maximumIconCount = reader.readUInt32();
  const sourceIcons = readList(reader, 1_024, () => ({
    sourceIconIndex: reader.readUInt32(),
    spriteId: reader.readUInt32(),
    frames: readList(reader, 65_535, () => ({
      sourceFrameIndex: reader.readUInt32(),
      sourcePaletteIndex: reader.readInt32(),
      sourceTextureIndex: reader.readInt32(),
      paletteBankIndex: reader.readInt32(),
      textureBankIndex: reader.readInt32(),
      width: reader.readInt32(),
      height: reader.readInt32(),
      sourceTexture: readTextureReference(reader),
      effectiveTexture: readTextureReference(reader),
      diagnostic: optionalString(reader.readString()),
    })),
  }));
  const additions = readList(reader, 1_024, () => ({
    spriteId: reader.readUInt32(),
    bankIndex: reader.readUInt32(),
    width: reader.readUInt32(),
    height: reader.readUInt32(),
    texture: readTextureReference(reader) ?? malformed('HUD addition texture is missing'),
  }));
  return {
    canRead,
    isDirty,
    canReplace,
    canAppend,
    authoringDisabledReason: optionalString(disabledReason),
    physicalBankCount,
    minimumAppendBank,
    minimumAppendSpriteId,
    maximumAppendSpriteId,
    maximumIconCount,
    sourceIcons,
    additions,
  };
}

function optionalString(value: string): string | undefined {
  return value.length === 0 ? undefined : value;
}

function readTextureReference(reader: PayloadReader): { id: string; kind: 'Texture' } | undefined {
  if (!reader.readBoolean()) return undefined;
  const id = reader.readString();
  const kind = reader.readString();
  if (kind !== 'Texture') malformed('Editor asset reference is not a texture');
  return { id, kind: 'Texture' };
}

function readReference(reader: PayloadReader): EditorSnapshot['references'][number] {
  const ownerEntityId = reader.readString();
  const domain = reader.readUInt32();
  const fieldKey = reader.readString();
  const nullable = reader.readBoolean();
  if (domain === 1) {
    const targetKind = enumValue(entityKinds, reader.readUInt32(), 'reference entity kind');
    const targetEntityId = reader.readBoolean() ? reader.readString() : undefined;
    const sourceValue = reader.readBoolean() ? reader.readUInt32() : undefined;
    const missing = reader.readBoolean();
    return { ownerEntityId, domain: 'entity', fieldKey, nullable, targetKind, targetEntityId, sourceValue, missing };
  }
  if (domain === 2) {
    const targetKind = reader.readString();
    const targetAssetId = reader.readBoolean() ? reader.readString() : undefined;
    const sourceValue = reader.readBoolean() ? reader.readUInt32() : undefined;
    const missing = reader.readBoolean();
    return { ownerEntityId, domain: 'asset', fieldKey, nullable, targetKind, targetAssetId, sourceValue, missing };
  }
  throw malformed(`Unknown reference domain ${domain}`);
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
  if (reader.readBoolean()) {
    value.collision = {
      kind: enumValue({ 1: 'solid', 2: 'playerBarrier' }, reader.readUInt32(), 'collision piece kind'),
      sourcePayloadIndex: reader.readUInt32(),
      sourcePieceIndex: reader.readUInt32(),
      faceCount: reader.readUInt32(),
      vertexCount: reader.readUInt32(),
      types: readList(reader, 256, () => ({ rawType: reader.readUInt32(), count: reader.readUInt32() })),
    };
    if (reader.readBoolean()) value.collision.attachment = {
      parentEntityId: reader.readString(),
      bindTransform: readTransform(reader),
    };
  }
  if (reader.readBoolean()) value.instancedCollision = {
    proxyAssetId: reader.readString(),
    recipe: readInstancedCollisionRecipe(reader),
    faceTypeOverrides: readList(reader, MAX_ENTITIES, () => ({
      faceIndex: reader.readUInt32(), rawType: reader.readUInt32(),
    })),
  };
  if (reader.readBoolean()) value.individualInstancedCollision = {
    proxyAssetId: reader.readString(),
    recipe: readInstancedCollisionRecipe(reader),
    faceTypeOverrides: readList(reader, MAX_ENTITIES, () => ({
      faceIndex: reader.readUInt32(), rawType: reader.readUInt32(),
    })),
  };
  if (reader.readBoolean()) value.instancedCollisionEnabled = reader.readBoolean();
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

function readInstancedCollisionRecipe(reader: PayloadReader): EditorInstancedCollisionRecipe {
  return {
    kind: enumValue({ 0: 'surface', 1: 'wrap', 2: 'hull' }, reader.readUInt32(), 'instanced collision recipe kind'),
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
