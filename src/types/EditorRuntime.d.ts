export interface ProjectVector3 {
  x: number;
  y: number;
  z: number;
}

export interface ProjectQuaternion extends ProjectVector3 {
  w: number;
}

export interface ProjectVector4 extends ProjectVector3 {
  w: number;
}

export interface ProjectTransform {
  position: ProjectVector3;
  rotation: ProjectQuaternion;
  scale: ProjectVector3;
}

export interface EditorTransformUpdate {
  entityId: string;
  transform: ProjectTransform;
}

export interface EditorPaletteOptimization {
  mappingVersion: 'paletteOptimization.v1';
  strength: number;
}

export interface EditorHudFrame {
  sourceFrameIndex: number;
  sourcePaletteIndex: number;
  sourceTextureIndex: number;
  paletteBankIndex: number;
  textureBankIndex: number;
  width: number;
  height: number;
  sourceTexture?: { id: string; kind: 'Texture' };
  effectiveTexture?: { id: string; kind: 'Texture' };
  diagnostic?: string;
}

export interface EditorHudIcon {
  sourceIconIndex: number;
  spriteId: number;
  frames: EditorHudFrame[];
}

export interface EditorHudAddition {
  spriteId: number;
  bankIndex: number;
  width: number;
  height: number;
  texture: { id: string; kind: 'Texture' };
}

export interface EditorHud {
  canRead: boolean;
  isDirty: boolean;
  canReplace: boolean;
  canAppend: boolean;
  authoringDisabledReason?: string;
  physicalBankCount: number;
  minimumAppendBank: number;
  minimumAppendSpriteId: number;
  maximumAppendSpriteId: number;
  maximumIconCount: number;
  sourceIcons: EditorHudIcon[];
  additions: EditorHudAddition[];
}

export interface EditorFxSourceTexture {
  sourceIndex: number;
  label: string;
  width: number;
  height: number;
  paletteOffset: number;
  pixelOffset: number;
  isSwizzled: boolean;
  sourceTexture?: { id: string; kind: 'Texture' };
  effectiveTexture?: { id: string; kind: 'Texture' };
  diagnostic?: string;
}

export interface EditorFxAddition {
  width: number;
  height: number;
  texture: { id: string; kind: 'Texture' };
}

export interface EditorFx {
  canRead: boolean;
  isDirty: boolean;
  canReplace: boolean;
  canAppend: boolean;
  authoringDisabledReason?: string;
  maximumTextureCount: number;
  sourceTextures: EditorFxSourceTexture[];
  additions: EditorFxAddition[];
}

export interface EditorAssetPlacement {
  assetId: string;
  kind: 'Tie' | 'Shrub' | 'Moby';
  classId: number;
  transform: ProjectTransform;
}

export interface EditorSkyShell {
  sourceShellIndex: number;
  order: number;
  initialRotationRadians: ProjectVector3;
  angularVelocityRadiansPerSecond: ProjectVector3;
}

export interface EditorCollisionPiece {
  kind: 'solid' | 'playerBarrier';
  sourcePayloadIndex: number;
  sourcePieceIndex: number;
  faceCount: number;
  vertexCount: number;
  types: Array<{ rawType: number; count: number }>;
  attachment?: { parentEntityId: string; bindTransform: ProjectTransform };
}

export interface EditorInstancedCollisionRecipe {
  kind: 'surface' | 'wrap' | 'hull';
  generatorVersion: number;
  recipeVersion: number;
  lodIndex: number;
  rawType: number;
  detailSize: number;
  sealOpeningSize: number;
  surfaceOffset: number;
  openBase: boolean;
  profileSections: number;
}

export interface EditorCollisionFaceType {
  faceIndex: number;
  rawType: number;
}

export interface EditorInstancedCollisionGenerationSettings {
  rawType: number;
  profileSections: number;
  surfaceLodIndex: number;
  useHull: boolean;
}

export interface EditorCollisionOctantCost {
  x: number;
  y: number;
  z: number;
  faceCount: number;
  vertexCount: number;
  quadCount: number;
  encodedByteCount: number;
  violations: string[];
  additionIds?: string[];
}

export interface EditorInstancedCollisionCombinedAnalysis {
  instanceCount: number;
  logicalFaceCount: number;
  occupiedOctantCount: number;
  duplicateFaceCount: number;
  hardViolationCount: number;
  octants: EditorCollisionOctantCost[];
  error?: string;
}

export interface EditorInstancedCollisionCandidate {
  token: string;
  preset: 'surface' | 'solidHull';
  label: string;
  recipe: EditorInstancedCollisionRecipe;
  encodedByteCount: number;
  vertexCount: number;
  faceCount: number;
  occupiedOctantCount: number;
  duplicateFaceCount: number;
  hardViolationCount: number;
  maximumDeviation: number;
  deviationSampleCount: number;
  octants: EditorCollisionOctantCost[];
  combinedAnalysis?: EditorInstancedCollisionCombinedAnalysis;
}

export interface EditorInstancedCollisionPreview {
  sourceAssetId: string;
  candidates: EditorInstancedCollisionCandidate[];
}

export interface EditorInstancedCollisionSourceInfo {
  sourceAssetId: string;
  surfaceLodIndices: number[];
}

export interface EditorLevelSettings {
  backgroundColor: [number, number, number];
  fogColor: [number, number, number];
  fogNearDistance: number;
  fogFarDistance: number;
  fogNearIntensity: number;
  fogFarIntensity: number;
}

export interface EditorEntity {
  id: string;
  name: string;
  layer: string;
  transform: ProjectTransform;
  asset?: { id: string; kind: string };
  provenance?: { game: string; level: number; section: string; sourceIndex: number };
  sourceClassId?: number;
  sourceClassName?: string;
  geometry?: {
    kind: 'cuboid' | 'sphere' | 'cylinder' | 'pill' | 'spline' | 'grindPath' | 'area'
      | 'directionalLight' | 'pointLight' | 'environmentSample' | 'environmentTransition'
      | 'camera' | 'ambientSound';
    points: ProjectVector4[];
  };
  skyShell?: EditorSkyShell;
  collision?: EditorCollisionPiece;
  instancedCollision?: EditorInstancedCollisionBinding;
  individualInstancedCollision?: EditorInstancedCollisionBinding;
  instancedCollisionEnabled?: boolean;
  mobyProperties?: EditorMobyPropertyDescriptor[];
  mobyPVar?: EditorMobyPVarDescriptor;
  transformModes: Array<'translate' | 'rotate' | 'scale'>;
  state: {
    dirty: boolean;
    hidden: boolean;
    disabled: boolean;
    locked: boolean;
    readOnly: boolean;
    invalid: boolean;
    missingAsset: boolean;
  };
}

export type EditorMobyPropertyValue =
  | { kind: 'integer'; value: number }
  | { kind: 'float'; value: number }
  | { kind: 'boolean'; value: boolean }
  | { kind: 'color'; value: [number, number, number] };

export interface EditorMobyPropertyDescriptor {
  key: string;
  label: string;
  value: EditorMobyPropertyValue;
  editable: boolean;
  integerMinimum?: number;
  integerMaximum?: number;
  floatMinimum?: number;
  floatMaximum?: number;
  unit?: string;
  help?: string;
  readOnlyReason?: string;
}

export type EditorMobyPVarValue =
  | { kind: 'integer'; value: string }
  | { kind: 'float'; value: number }
  | { kind: 'boolean'; value: boolean }
  | { kind: 'color'; value: number[] }
  | { kind: 'vector'; value: number[] }
  | { kind: 'bytes'; value: string }
  | { kind: 'reference'; value?: string };

export interface EditorMobyPVarOption {
  key: string;
  label: string;
  value: string;
}

export interface EditorMobyPVarFieldDescriptor {
  path: string;
  label: string;
  offset: number;
  length: number;
  kind: 'group' | 'integer' | 'float' | 'boolean' | 'color' | 'vector'
    | 'choice' | 'flags' | 'reference' | 'bytes' | 'unknown';
  value?: EditorMobyPVarValue;
  editable: boolean;
  invalid: boolean;
  help?: string;
  minimum?: string;
  maximum?: string;
  options?: EditorMobyPVarOption[];
  reference?: {
    targetKind: EditorEntityKind;
    targetEntityId?: string;
    nullable: boolean;
    sourceValue: number;
    missing: boolean;
  };
  children: EditorMobyPVarFieldDescriptor[];
}

export interface EditorMobyPVarDescriptor {
  datasetId: string;
  datasetVersion: number;
  schemaVersion: number;
  schemaSource: string;
  schemaFingerprint: string;
  stateFingerprint: string;
  length: number;
  hasData: boolean;
  canInitialize: boolean;
  diagnostic?: string;
  rawData?: Uint8Array;
  modifiedByteMask?: Uint8Array;
  fields: EditorMobyPVarFieldDescriptor[];
}

export interface EditorInstancedCollisionBinding {
  proxyAssetId: string;
  recipe: EditorInstancedCollisionRecipe;
  faceTypeOverrides: EditorCollisionFaceType[];
}

export type EditorEntityKind = 'entity' | 'moby' | 'tie' | 'shrub' | 'tfrag'
  | 'cuboid' | 'sphere' | 'cylinder' | 'pill' | 'spline' | 'grindPath' | 'area'
  | 'collision' | 'skyShell' | 'directionalLight' | 'pointLight'
  | 'environmentSample' | 'environmentTransition' | 'camera' | 'ambientSound';

interface EditorReferenceBase {
  ownerEntityId: string;
  fieldKey: string;
  nullable: boolean;
  sourceValue?: number;
  missing: boolean;
  datasetSource?: string;
}

export type EditorReference =
  | EditorReferenceBase & {
    domain: 'entity';
    targetKind: EditorEntityKind;
    targetEntityId?: string;
  }
  | EditorReferenceBase & {
    domain: 'asset';
    targetKind: string;
    targetAssetId?: string;
  };

export interface EditorGroup {
  id: string;
  name: string;
  members: string[];
  missingMembers: string[];
}

export interface EditorMapGroup {
  kind: 'moby' | 'tie' | 'shrub';
  sourceIndex: number;
  members: string[];
  missingSourceIndices: number[];
}

export type EditorCommand =
  | { id: string; kind: 'setSelection'; entityIds: string[] }
  | { id: string; kind: 'renameProject'; entityIds: []; text: string }
  | { id: string; kind: 'updateTransform'; entityIds: [string]; transform: ProjectTransform }
  | { id: string; kind: 'updateTransforms'; entityIds: string[]; transforms: EditorTransformUpdate[] }
  | { id: string; kind: 'renameEntity'; entityIds: [string]; text: string }
  | { id: string; kind: 'setEntityLayer'; entityIds: string[]; text: string }
  | {
    id: string;
    kind: 'setEntityState';
    entityIds: string[];
    state: { hidden?: boolean; disabled?: boolean; locked?: boolean };
  }
  | { id: string; kind: 'undo' | 'redo' | 'pasteEntities'; entityIds: [] }
  | { id: string; kind: 'deleteEntities' | 'duplicateEntities' | 'copyEntities'; entityIds: string[] }
  | { id: string; kind: 'updateLevelSettings'; entityIds: []; levelSettings: EditorLevelSettings }
  | {
    id: string;
    kind: 'updatePaletteOptimization';
    entityIds: [];
    paletteOptimization: EditorPaletteOptimization;
  }
  | { id: string; kind: 'updateSplinePoints'; entityIds: [string]; points: ProjectVector4[] }
  | { id: string; kind: 'createEntityFromAsset'; entityIds: []; placement: EditorAssetPlacement }
  | { id: string; kind: 'addSkyShellFromAsset'; entityIds: []; source: { assetId: string; shellIndex: number } }
  | {
    id: string;
    kind: 'updateSkyShell';
    entityIds: [string];
    update: { initialRotationRadians?: ProjectVector3; angularVelocityRadiansPerSecond?: ProjectVector3 };
  }
  | { id: string; kind: 'reorderSkyShell'; entityIds: [string]; destinationOrder: number }
  | { id: string; kind: 'removeInstancedCollisionProxy'; entityIds: [string] }
  | { id: string; kind: 'setInstancedCollisionEnabled'; entityIds: string[]; enabled: boolean | null }
  | { id: string; kind: 'setInstancedCollisionRawType'; entityIds: [string]; rawType: number }
  | {
    id: string;
    kind: 'setInstancedCollisionFaceTypes';
    entityIds: [string];
    expectedProxyAssetId: string;
    faceTypes: EditorCollisionFaceType[];
  }
  | {
    id: string;
    kind: 'setEntityReference';
    entityIds: [string];
    reference: { fieldKey: string; sourceValue?: number; targetEntityId?: string };
  }
  | {
    id: string;
    kind: 'replaceHudTexture';
    entityIds: [];
    sourceAssetId: string;
    imageFormat: 'png' | 'pif';
    imageBytes: Uint8Array;
  }
  | { id: string; kind: 'removeHudTextureOverride'; entityIds: []; sourceAssetId: string }
  | {
    id: string;
    kind: 'addHudIcon';
    entityIds: [];
    spriteId: number;
    bankIndex: number;
    imageFormat: 'png' | 'pif';
    imageBytes: Uint8Array;
  }
  | { id: string; kind: 'removeHudIcon'; entityIds: []; spriteId: number }
  | {
    id: string;
    kind: 'replaceFxTexture';
    entityIds: [];
    sourceAssetId: string;
    imageFormat: 'png' | 'pif';
    imageBytes: Uint8Array;
  }
  | { id: string; kind: 'removeFxTextureOverride'; entityIds: []; sourceAssetId: string }
  | {
    id: string;
    kind: 'addFxTexture';
    entityIds: [];
    imageFormat: 'png' | 'pif';
    imageBytes: Uint8Array;
  }
  | { id: string; kind: 'removeFxTexture'; entityIds: []; index: number }
  | {
    id: string;
    kind: 'setMobyInstanceProperty';
    entityIds: string[];
    property: { fieldKey: string; expectedClassId: number; value: EditorMobyPropertyValue };
  }
  | { id: string; kind: 'importMobyDexEntry'; entityIds: []; entryJson: Uint8Array }
  | { id: string; kind: 'removeMobyDexEntry'; entityIds: []; game: string; oClass: number }
  | { id: string; kind: 'initializeMobyPVar'; entityIds: [string] }
  | {
    id: string;
    kind: 'setMobyPVarField';
    entityIds: [string];
    pvar: {
      fieldPath: string;
      expectedClassId: number;
      expectedDatasetId: string;
      expectedDatasetVersion: number;
      expectedSchemaVersion: number;
      expectedSchemaFingerprint: string;
      expectedStateFingerprint: string;
      value: EditorMobyPVarValue;
    };
  }
  | { id: string; kind: 'createGroup'; entityIds: []; text: string }
  | { id: string; kind: 'renameGroup'; entityIds: []; groupId: string; text: string }
  | { id: string; kind: 'deleteGroup'; entityIds: []; groupId: string }
  | { id: string; kind: 'reorderGroup'; entityIds: []; groupId: string; destinationOrder: number }
  | { id: string; kind: 'addGroupMembers' | 'removeGroupMembers'; entityIds: string[]; groupId: string };

export interface EditorEvent {
  sequence: number;
  createdUnixMilliseconds: number;
  kind: 'projectOpened' | 'projectChanged' | 'selectionChanged' | 'projectSaved'
    | 'recoveryWritten' | 'diagnosticRaised' | 'projectClosed';
  commandId?: string;
  entityIds: string[];
  message?: string;
}

export interface EditorSnapshot {
  projectPath: string;
  projectId: string;
  projectName: string;
  target: {
    game: string;
    region: string;
    revision: string;
    bakeProfile: string;
    paletteOptimization: EditorPaletteOptimization;
  };
  baseLevel: {
    game: string;
    region: string;
    revision: string;
    level: number;
    sourceFingerprint: string;
    missingAssetCount: number;
  };
  levelSettings?: EditorLevelSettings;
  hud?: EditorHud;
  fx?: EditorFx;
  entities: EditorEntity[];
  references: EditorReference[];
  groups: EditorGroup[];
  mapGroups: EditorMapGroup[];
  selection: string[];
  isDirty: boolean;
  canUndo: boolean;
  canRedo: boolean;
  canPaste: boolean;
  lastEventSequence: number;
  capabilities: string[];
  tools: { id: string; label: string; capability: string }[];
  diagnostics: { code: string; severity: 'info' | 'warning' | 'error'; message: string }[];
}
