export interface ProjectVector3 {
  x: number;
  y: number;
  z: number;
}

export interface ProjectQuaternion extends ProjectVector3 {
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

export interface EditorEntity {
  id: string;
  name: string;
  layer: string;
  transform: ProjectTransform;
  asset?: { id: string; kind: string };
  provenance?: { game: string; level: number; section: string; sourceIndex: number };
  sourceClassId?: number;
  state: {
    dirty: boolean;
    hidden: boolean;
    disabled: boolean;
    locked: boolean;
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
  };

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
  entities: EditorEntity[];
  selection: string[];
  isDirty: boolean;
  migrationPending: boolean;
  lastEventSequence: number;
  capabilities: string[];
  tools: { id: string; label: string; capability: string }[];
  diagnostics: { code: string; severity: 'info' | 'warning' | 'error'; message: string }[];
}
