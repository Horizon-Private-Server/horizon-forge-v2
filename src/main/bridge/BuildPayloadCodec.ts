import { Buffer } from 'node:buffer';

import type { UyaBuildPatchRequest, UyaBuildPlanRequest } from '../../types/BridgePayloads.js';
import type { BuildLayerId, BuildPatchProgress, BuildPatchResult, BuildPlan } from '../../types/ForgeApi.js';
import { PayloadReader, PayloadWriter } from './PayloadIO.js';

export function encodeBuildPatchRequest(value: UyaBuildPatchRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.projectRoot);
  writer.writeString(value.catalogRoot);
  writer.writeString(value.cleanSourceIso);
  writer.writeString(value.developmentIso);
  writer.writeString(value.sourceFingerprint);
  writer.writeStrings(value.acknowledgedWarnings);
  writer.writeBoolean(value.forceFullImage);
  writer.writeStrings(value.includedLayers);
  return writer.toBuffer();
}

export function encodeBuildPlanRequest(value: UyaBuildPlanRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.projectRoot);
  writer.writeString(value.catalogRoot);
  return writer.toBuffer();
}

export function decodeBuildPlan(payload: Uint8Array): BuildPlan {
  const reader = new PayloadReader(payload);
  const count = reader.readUInt32();
  const layers = Array.from({ length: count }, () => ({
    layer: reader.readString() as BuildLayerId,
    state: reader.readString() as BuildPlan['layers'][number]['state'],
    canDefer: reader.readBoolean(),
  }));
  reader.complete();
  return { layers };
}

export function decodeBuildPatchProgress(payload: Uint8Array): BuildPatchProgress {
  const reader = new PayloadReader(payload);
  const value = {
    phase: reader.readString(),
    completed: reader.readUInt64(),
    total: reader.readUInt64(),
    message: reader.readString(),
  };
  reader.complete();
  return value;
}

export function decodeBuildPatchResult(payload: Uint8Array): BuildPatchResult {
  const reader = new PayloadReader(payload);
  const succeeded = reader.readBoolean();
  const requiresWarningAcknowledgement = reader.readBoolean();
  const warningCodes = reader.readStrings();
  const diagnostics = reader.readStrings();
  const message = reader.readString();
  const nextAction = reader.readString();
  const developmentIsoPath = reader.readString() || undefined;
  const patchModeValue = reader.readString();
  const outputLevelWadSha256 = reader.readString() || undefined;
  const bakedLayerCount = reader.readUInt32();
  const bakeWasCurrent = reader.readBoolean();
  reader.complete();
  const patchMode = patchModeValue === 'InPlace' || patchModeValue === 'FullImageReplacement'
    ? patchModeValue : undefined;
  return {
    succeeded,
    requiresWarningAcknowledgement,
    warningCodes,
    diagnostics,
    message,
    nextAction,
    developmentIsoPath,
    patchMode,
    outputLevelWadSha256,
    bakedLayerCount,
    bakeWasCurrent,
  };
}
