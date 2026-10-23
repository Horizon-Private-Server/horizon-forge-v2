import type {
  ContentImportCapabilityMatrix,
  ContentImportCapabilityRequest,
  ContentImportApplyRequest,
  ContentImportPlan,
  ContentImportPlanRequest,
  ContentImportRemovalPlan,
  ContentImportMode,
  ContentImportSourceState,
} from '../../types/ContentImport.js';
import { editorEntityKindIds, editorEntityKinds } from './EditorEntityKinds.ts';
import { PayloadReader, PayloadWriter, malformed } from './PayloadIO.ts';

const MAX_FAMILIES = 32;
const MAX_PLAN_ITEMS = 100_000;
const modeIds: Record<ContentImportMode, number> = {
  unsupported: 0, add: 1, replace: 2, addAndReplace: 3,
};
const modes = Object.fromEntries(Object.entries(modeIds).map(([value, id]) => [id, value])) as
  Record<number, ContentImportMode>;
const stateIds: Record<ContentImportSourceState, number> = {
  unknown: 0, absent: 1, empty: 2, present: 3, malformed: 4,
};
const states = Object.fromEntries(Object.entries(stateIds).map(([value, id]) => [id, value])) as
  Record<number, ContentImportSourceState>;
export function encodeContentImportCapabilityRequest(value: ContentImportCapabilityRequest): Buffer {
  if (value.inventory.length > MAX_FAMILIES) malformed('Import family inventory exceeds limit');
  const writer = new PayloadWriter();
  writer.writeString(value.targetGame);
  writer.writeString(value.targetRegion);
  writer.writeString(value.targetRevision);
  writer.writeUInt32(value.inventory.length);
  value.inventory.forEach((item) => {
    writer.writeString(item.family);
    writer.writeUInt32(stateIds[item.state]);
    writer.writeBoolean(item.diagnostic !== undefined);
    if (item.diagnostic !== undefined) writer.writeString(item.diagnostic);
  });
  return writer.toBuffer();
}

export function decodeContentImportCapabilityRequest(payload: Uint8Array): ContentImportCapabilityRequest {
  const reader = new PayloadReader(payload);
  const targetGame = reader.readString();
  const targetRegion = reader.readString();
  const targetRevision = reader.readString();
  const count = readCount(reader, 'Import family inventory');
  const inventory = Array.from({ length: count }, () => ({
    family: reader.readString(),
    state: readState(reader),
    ...(reader.readBoolean() ? { diagnostic: reader.readString() } : {}),
  }));
  reader.complete();
  return { targetGame, targetRegion, targetRevision, inventory };
}

export function encodeContentImportCapabilityMatrix(value: ContentImportCapabilityMatrix): Buffer {
  if (value.families.length > MAX_FAMILIES) malformed('Import capability matrix exceeds limit');
  const writer = new PayloadWriter();
  writer.writeUInt32(value.schemaVersion);
  writer.writeString(value.game);
  writer.writeString(value.region);
  writer.writeString(value.revision);
  writer.writeUInt32(value.families.length);
  value.families.forEach((family) => {
    writer.writeString(family.family);
    writer.writeString(family.label);
    writer.writeUInt32(modeIds[family.mode]);
    writer.writeUInt32(stateIds[family.sourceState]);
    writer.writeBoolean(family.implementationReady);
    writer.writeBoolean(family.selectable);
    writer.writeBoolean(family.defaultSelectedWhenReady);
    writer.writeBoolean(family.defaultSelected);
    writer.writeBoolean(family.disabledReason !== undefined);
    if (family.disabledReason !== undefined) writer.writeString(family.disabledReason);
    writer.writeString(family.ownership);
    writer.writeStrings(family.dependencies);
    writer.writeString(family.conflictNamespace);
    writer.writeString(family.nativeSemantics);
    writer.writeString(family.protectedSurvivorPolicy);
    writer.writeString(family.noEditIdentity);
    writer.writeStrings(family.limits);
    writer.writeStrings(family.evidence);
  });
  return writer.toBuffer();
}

export function decodeContentImportCapabilityMatrix(payload: Uint8Array): ContentImportCapabilityMatrix {
  const reader = new PayloadReader(payload);
  const schemaVersion = reader.readUInt32();
  const game = reader.readString();
  const region = reader.readString();
  const revision = reader.readString();
  const count = readCount(reader, 'Import capability matrix');
  const families = Array.from({ length: count }, () => {
    const family = reader.readString();
    const label = reader.readString();
    const mode = readMode(reader);
    const sourceState = readState(reader);
    const implementationReady = reader.readBoolean();
    const selectable = reader.readBoolean();
    const defaultSelectedWhenReady = reader.readBoolean();
    const defaultSelected = reader.readBoolean();
    const disabledReason = reader.readBoolean() ? reader.readString() : undefined;
    const ownership = reader.readString();
    const dependencies = reader.readStrings();
    const conflictNamespace = reader.readString();
    const nativeSemantics = reader.readString();
    const protectedSurvivorPolicy = reader.readString();
    const noEditIdentity = reader.readString();
    const limits = reader.readStrings();
    const evidence = reader.readStrings();
    return {
      family, label, mode, sourceState, implementationReady, selectable,
      defaultSelectedWhenReady, defaultSelected, disabledReason, ownership, dependencies,
      conflictNamespace, nativeSemantics, protectedSurvivorPolicy, noEditIdentity, limits, evidence,
    };
  });
  reader.complete();
  return { schemaVersion, game, region, revision, families };
}

export function encodeContentImportPlanRequest(value: ContentImportPlanRequest): Buffer {
  if (value.selections.length > MAX_FAMILIES) malformed('Import selection list exceeds limit');
  const writer = new PayloadWriter();
  writer.writeString(value.projectPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.sourceIsoPath);
  writer.writeString(value.sourceFingerprint);
  writer.writeUInt32(value.sourceLevel);
  writer.writeUInt32(value.selections.length);
  value.selections.forEach((selection) => {
    writer.writeString(selection.family);
    writer.writeUInt32(modeIds[selection.mode]);
  });
  return writer.toBuffer();
}

export function decodeContentImportPlanRequest(payload: Uint8Array): ContentImportPlanRequest {
  const reader = new PayloadReader(payload);
  const projectPath = reader.readString();
  const catalogRootPath = reader.readString();
  const sourceIsoPath = reader.readString();
  const sourceFingerprint = reader.readString();
  const sourceLevel = reader.readUInt32();
  const count = readCount(reader, 'Import selection list');
  const selections = Array.from({ length: count }, () => {
    const family = reader.readString();
    const mode = readMode(reader);
    if (mode !== 'add' && mode !== 'replace') malformed('Import selection must use add or replace mode');
    return { family, mode };
  });
  reader.complete();
  return { projectPath, catalogRootPath, sourceIsoPath, sourceFingerprint, sourceLevel, selections };
}

export function encodeContentImportApplyRequest(value: ContentImportApplyRequest): Buffer {
  if (value.selections.length > MAX_FAMILIES) malformed('Import selection list exceeds limit');
  const writer = new PayloadWriter();
  writer.writeString(value.projectPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.sourceIsoPath);
  writer.writeString(value.sourceFingerprint);
  writer.writeUInt32(value.sourceLevel);
  writer.writeString(value.batchId);
  writer.writeUInt32(value.selections.length);
  value.selections.forEach((selection) => {
    writer.writeString(selection.family);
    writer.writeUInt32(modeIds[selection.mode]);
  });
  if (value.referenceChoices.length > MAX_PLAN_ITEMS) malformed('Import reference choice list exceeds limit');
  writer.writeUInt32(value.referenceChoices.length);
  value.referenceChoices.forEach((choice) => {
    writer.writeInt32(choice.ownerSourceIndex);
    writer.writeString(choice.fieldKey);
    writer.writeInt32(choice.sourceValue);
    writer.writeUInt32(choice.action === 'map' ? 1 : 2);
    writer.writeBoolean(choice.targetEntityId !== undefined);
    if (choice.targetEntityId !== undefined) writer.writeString(choice.targetEntityId);
  });
  return writer.toBuffer();
}

export function decodeContentImportApplyRequest(payload: Uint8Array): ContentImportApplyRequest {
  const reader = new PayloadReader(payload);
  const projectPath = reader.readString();
  const catalogRootPath = reader.readString();
  const sourceIsoPath = reader.readString();
  const sourceFingerprint = reader.readString();
  const sourceLevel = reader.readUInt32();
  const batchId = reader.readString();
  const count = readCount(reader, 'Import selection list');
  const selections = Array.from({ length: count }, () => {
    const family = reader.readString();
    const mode = readMode(reader);
    if (mode !== 'add' && mode !== 'replace') malformed('Import selection must use add or replace mode');
    return { family, mode };
  });
  const referenceCount = readPlanCount(reader, 'Import reference choice list');
  const referenceChoices = Array.from({ length: referenceCount }, () => {
    const ownerSourceIndex = reader.readInt32();
    const fieldKey = reader.readString();
    const sourceValue = reader.readInt32();
    const actionId = reader.readUInt32();
    if (actionId !== 1 && actionId !== 2) malformed(`Unknown import reference action ${actionId}`);
    const targetEntityId = reader.readBoolean() ? reader.readString() : undefined;
    return { ownerSourceIndex, fieldKey, sourceValue, action: actionId === 1 ? 'map' as const : 'clear' as const,
      targetEntityId };
  });
  reader.complete();
  return {
    projectPath, catalogRootPath, sourceIsoPath, sourceFingerprint, sourceLevel, batchId,
    selections, referenceChoices,
  };
}

export function encodeContentImportPlan(value: ContentImportPlan): Buffer {
  if (value.families.length > MAX_FAMILIES) malformed('Import plan family list exceeds limit');
  const writer = new PayloadWriter();
  writer.writeUInt32(value.schemaVersion);
  writer.writeUInt32(value.capabilitySchemaVersion);
  writer.writeString(value.batchId);
  writer.writeString(value.game);
  writer.writeString(value.region);
  writer.writeString(value.revision);
  writer.writeString(value.sourceFingerprint);
  writer.writeString(value.sourceLevelHash);
  writer.writeUInt32(value.sourceLevel);
  writer.writeString(value.targetProjectId);
  writer.writeString(value.targetFingerprint);
  writer.writeUInt32(value.targetLevel);
  writer.writeUInt64(value.estimatedBytes);
  writer.writeUInt32(value.families.length);
  value.families.forEach((family) => {
    writer.writeString(family.family);
    writer.writeString(family.label);
    writer.writeUInt32(modeIds[family.supportedMode]);
    writer.writeUInt32(modeIds[family.mode]);
    writer.writeUInt32(stateIds[family.sourceState]);
    writer.writeBoolean(family.selected);
    writer.writeBoolean(family.defaultSelected);
    writer.writeBoolean(family.selectable);
    writePlanItems(writer, family.add);
    writePlanItems(writer, family.reuse);
    writePlanItems(writer, family.remap);
    writePlanItems(writer, family.remove);
    writePlanItems(writer, family.protect);
    writer.writeStrings(family.dependencyReasons);
    writePlanItems(writer, family.conflicts);
    writer.writeUInt64(family.estimatedBytes);
    writer.writeStrings(family.warnings);
    writer.writeStrings(family.blockers);
  });
  writer.writeStrings(value.warnings);
  writer.writeStrings(value.blockers);
  if (value.referenceRequirements.length > MAX_PLAN_ITEMS)
    malformed('Import reference requirement list exceeds limit');
  writer.writeUInt32(value.referenceRequirements.length);
  value.referenceRequirements.forEach((requirement) => {
    writer.writeString(requirement.family);
    writer.writeInt32(requirement.ownerSourceIndex);
    writer.writeString(requirement.fieldKey);
    writer.writeInt32(requirement.sourceValue);
    const targetKind = editorEntityKindIds[requirement.targetKind];
    if (targetKind === undefined) malformed(`Unknown import reference target kind ${requirement.targetKind}`);
    writer.writeUInt32(targetKind);
    writer.writeBoolean(requirement.nullable);
    writePlanItems(writer, requirement.candidateEntityIds);
  });
  return writer.toBuffer();
}

export function decodeContentImportPlan(payload: Uint8Array): ContentImportPlan {
  const reader = new PayloadReader(payload);
  const schemaVersion = reader.readUInt32();
  const capabilitySchemaVersion = reader.readUInt32();
  const batchId = reader.readString();
  const game = reader.readString();
  const region = reader.readString();
  const revision = reader.readString();
  const sourceFingerprint = reader.readString();
  const sourceLevelHash = reader.readString();
  const sourceLevel = reader.readUInt32();
  const targetProjectId = reader.readString();
  const targetFingerprint = reader.readString();
  const targetLevel = reader.readUInt32();
  const estimatedBytes = reader.readUInt64();
  const count = readCount(reader, 'Import plan family list');
  const families = Array.from({ length: count }, () => ({
    family: reader.readString(),
    label: reader.readString(),
    supportedMode: readMode(reader),
    mode: readMode(reader),
    sourceState: readState(reader),
    selected: reader.readBoolean(),
    defaultSelected: reader.readBoolean(),
    selectable: reader.readBoolean(),
    add: readPlanItems(reader),
    reuse: readPlanItems(reader),
    remap: readPlanItems(reader),
    remove: readPlanItems(reader),
    protect: readPlanItems(reader),
    dependencyReasons: reader.readStrings(),
    conflicts: readPlanItems(reader),
    estimatedBytes: reader.readUInt64(),
    warnings: reader.readStrings(),
    blockers: reader.readStrings(),
  }));
  const warnings = reader.readStrings();
  const blockers = reader.readStrings();
  const requirementCount = readPlanCount(reader, 'Import reference requirement list');
  const referenceRequirements = Array.from({ length: requirementCount }, () => {
    const family = reader.readString();
    const ownerSourceIndex = reader.readInt32();
    const fieldKey = reader.readString();
    const sourceValue = reader.readInt32();
    const targetKindId = reader.readUInt32();
    const targetKind = editorEntityKinds[targetKindId]
      ?? malformed(`Unknown import reference target kind ${targetKindId}`);
    const nullable = reader.readBoolean();
    const candidateEntityIds = readPlanItems(reader);
    return { family, ownerSourceIndex, fieldKey, sourceValue, targetKind, nullable, candidateEntityIds };
  });
  reader.complete();
  return {
    schemaVersion, capabilitySchemaVersion, batchId, game, region, revision,
    sourceFingerprint, sourceLevelHash, sourceLevel, targetProjectId, targetFingerprint,
    targetLevel, estimatedBytes, families, warnings, blockers, referenceRequirements,
  };
}

export function encodeContentImportRemovalPlanRequest(projectPath: string, batchId: string): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(projectPath);
  writer.writeString(batchId);
  return writer.toBuffer();
}

export function decodeContentImportRemovalPlanRequest(payload: Uint8Array): {
  projectPath: string;
  batchId: string;
} {
  const reader = new PayloadReader(payload);
  const result = { projectPath: reader.readString(), batchId: reader.readString() };
  reader.complete();
  return result;
}

export function encodeContentImportRemovalApplyRequest(
  projectPath: string,
  batchId: string,
  targetFingerprint: string,
): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(projectPath);
  writer.writeString(batchId);
  writer.writeString(targetFingerprint);
  return writer.toBuffer();
}

export function decodeContentImportRemovalApplyRequest(payload: Uint8Array): {
  projectPath: string;
  batchId: string;
  targetFingerprint: string;
} {
  const reader = new PayloadReader(payload);
  const result = {
    projectPath: reader.readString(), batchId: reader.readString(), targetFingerprint: reader.readString(),
  };
  reader.complete();
  return result;
}

export function encodeContentImportRemovalPlan(value: ContentImportRemovalPlan): Buffer {
  if (value.survivors.length > MAX_PLAN_ITEMS) malformed('Import removal survivor list exceeds limit');
  const writer = new PayloadWriter();
  writer.writeUInt32(value.schemaVersion);
  writer.writeString(value.batchId);
  writer.writeString(value.targetFingerprint);
  writePlanItems(writer, value.delete);
  writer.writeUInt32(value.survivors.length);
  value.survivors.forEach((survivor) => {
    writer.writeString(survivor.stableId);
    writer.writeString(survivor.reason);
  });
  writePlanItems(writer, value.shared);
  writePlanItems(writer, value.protected);
  writer.writeStrings(value.warnings);
  return writer.toBuffer();
}

export function decodeContentImportRemovalPlan(payload: Uint8Array): ContentImportRemovalPlan {
  const reader = new PayloadReader(payload);
  const schemaVersion = reader.readUInt32();
  const batchId = reader.readString();
  const targetFingerprint = reader.readString();
  const deleteItems = readPlanItems(reader);
  const survivorCount = readPlanCount(reader, 'Import removal survivor list');
  const survivors = Array.from({ length: survivorCount }, () => ({
    stableId: reader.readString(), reason: reader.readString(),
  }));
  const shared = readPlanItems(reader);
  const protectedItems = readPlanItems(reader);
  const warnings = reader.readStrings();
  reader.complete();
  return {
    schemaVersion, batchId, targetFingerprint, delete: deleteItems,
    survivors, shared, protected: protectedItems, warnings,
  };
}

function readCount(reader: PayloadReader, label: string): number {
  const count = reader.readUInt32();
  if (count > MAX_FAMILIES) malformed(`${label} exceeds limit`);
  return count;
}

function readPlanCount(reader: PayloadReader, label: string): number {
  const count = reader.readUInt32();
  if (count > MAX_PLAN_ITEMS) malformed(`${label} exceeds limit`);
  return count;
}

function readMode(reader: PayloadReader): ContentImportMode {
  const id = reader.readUInt32();
  return modes[id] ?? malformed(`Unknown import mode ${id}`);
}

function readState(reader: PayloadReader): ContentImportSourceState {
  const id = reader.readUInt32();
  return states[id] ?? malformed(`Unknown import source state ${id}`);
}

function readPlanItems(reader: PayloadReader): string[] {
  const count = reader.readUInt32();
  if (count > MAX_PLAN_ITEMS) malformed('Import plan item list exceeds limit');
  return Array.from({ length: count }, () => reader.readString());
}

function writePlanItems(writer: PayloadWriter, values: string[]): void {
  if (values.length > MAX_PLAN_ITEMS) malformed('Import plan item list exceeds limit');
  writer.writeUInt32(values.length);
  values.forEach((value) => writer.writeString(value));
}
