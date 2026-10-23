import type { EditorEntityKind } from './EditorRuntime.js';

export type ContentImportMode = 'unsupported' | 'add' | 'replace' | 'addAndReplace';
export type ContentImportSourceState = 'unknown' | 'absent' | 'empty' | 'present' | 'malformed';

export interface ContentImportOptions {
  levels: { value: number; label: string }[];
  warnings: string[];
}

export interface ContentImportFamilyInventory {
  family: string;
  state: ContentImportSourceState;
  diagnostic?: string;
}

export interface ContentImportCapabilityRequest {
  targetGame: string;
  targetRegion: string;
  targetRevision: string;
  inventory: ContentImportFamilyInventory[];
}

export interface ContentImportFamilyCapability {
  family: string;
  label: string;
  mode: ContentImportMode;
  sourceState: ContentImportSourceState;
  implementationReady: boolean;
  selectable: boolean;
  defaultSelectedWhenReady: boolean;
  defaultSelected: boolean;
  disabledReason?: string;
  ownership: string;
  dependencies: string[];
  conflictNamespace: string;
  nativeSemantics: string;
  protectedSurvivorPolicy: string;
  noEditIdentity: string;
  limits: string[];
  evidence: string[];
}

export interface ContentImportCapabilityMatrix {
  schemaVersion: number;
  game: string;
  region: string;
  revision: string;
  families: ContentImportFamilyCapability[];
}

export interface ContentImportSelection {
  family: string;
  mode: 'add' | 'replace';
}

export interface ContentImportPlanRequest {
  projectPath: string;
  catalogRootPath: string;
  sourceIsoPath: string;
  sourceFingerprint: string;
  sourceLevel: number;
  selections: ContentImportSelection[];
}

export interface ContentImportApplyRequest extends ContentImportPlanRequest {
  batchId: string;
  referenceChoices: ContentImportReferenceChoice[];
}

export interface ContentImportReferenceChoice {
  ownerSourceIndex: number;
  fieldKey: string;
  sourceValue: number;
  action: 'map' | 'clear';
  targetEntityId?: string;
}

export interface ContentImportReferenceRequirement {
  family: string;
  ownerSourceIndex: number;
  fieldKey: string;
  sourceValue: number;
  targetKind: EditorEntityKind;
  nullable: boolean;
  candidateEntityIds: string[];
}

export interface ContentImportFamilyPlan {
  family: string;
  label: string;
  supportedMode: ContentImportMode;
  mode: ContentImportMode;
  sourceState: ContentImportSourceState;
  selected: boolean;
  defaultSelected: boolean;
  selectable: boolean;
  add: string[];
  reuse: string[];
  remap: string[];
  remove: string[];
  protect: string[];
  dependencyReasons: string[];
  conflicts: string[];
  estimatedBytes: number;
  warnings: string[];
  blockers: string[];
}

export interface ContentImportPlan {
  schemaVersion: number;
  capabilitySchemaVersion: number;
  batchId: string;
  game: string;
  region: string;
  revision: string;
  sourceFingerprint: string;
  sourceLevelHash: string;
  sourceLevel: number;
  targetProjectId: string;
  targetFingerprint: string;
  targetLevel: number;
  estimatedBytes: number;
  families: ContentImportFamilyPlan[];
  warnings: string[];
  blockers: string[];
  referenceRequirements: ContentImportReferenceRequirement[];
}

export interface ContentImportRemovalPlan {
  schemaVersion: number;
  batchId: string;
  targetFingerprint: string;
  delete: string[];
  survivors: { stableId: string; reason: string }[];
  shared: string[];
  protected: string[];
  warnings: string[];
}
