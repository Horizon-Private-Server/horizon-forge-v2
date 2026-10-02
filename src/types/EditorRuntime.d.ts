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
  geometry?: {
    kind: 'cuboid' | 'sphere' | 'cylinder' | 'pill' | 'spline' | 'grindPath' | 'area'
      | 'directionalLight' | 'pointLight' | 'environmentSample' | 'environmentTransition'
      | 'camera' | 'ambientSound';
    points: ProjectVector4[];
  };
  skyShell?: EditorSkyShell;
  collision?: EditorCollisionPiece;
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
  | { id: string; kind: 'updateSplinePoints'; entityIds: [string]; points: ProjectVector4[] }
  | { id: string; kind: 'createEntityFromAsset'; entityIds: []; placement: EditorAssetPlacement }
  | { id: string; kind: 'addSkyShellFromAsset'; entityIds: []; source: { assetId: string; shellIndex: number } }
  | {
    id: string;
    kind: 'updateSkyShell';
    entityIds: [string];
    update: { initialRotationRadians?: ProjectVector3; angularVelocityRadiansPerSecond?: ProjectVector3 };
  }
  | { id: string; kind: 'reorderSkyShell'; entityIds: [string]; destinationOrder: number };

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
  target: { game: string; region: string; revision: string; bakeProfile: string };
  baseLevel: {
    game: string;
    region: string;
    revision: string;
    level: number;
    sourceFingerprint: string;
    missingAssetCount: number;
  };
  levelSettings?: EditorLevelSettings;
  entities: EditorEntity[];
  selection: string[];
  isDirty: boolean;
  migrationPending: boolean;
  canUndo: boolean;
  canRedo: boolean;
  canPaste: boolean;
  lastEventSequence: number;
  capabilities: string[];
  tools: { id: string; label: string; capability: string }[];
  diagnostics: { code: string; severity: 'info' | 'warning' | 'error'; message: string }[];
}
