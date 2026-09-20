import { Buffer } from 'node:buffer';

import type { CatalogMaintenancePreview } from '../../types/ForgeApi.js';
import type {
  CatalogCollectionRequest,
  CatalogMaintenanceRequest,
  ProjectAssetRepairRequest,
} from '../../types/BridgePayloads.js';
import { PayloadReader, PayloadWriter } from './PayloadIO.ts';

export function encodeProjectAssetRepairRequest(value: ProjectAssetRepairRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.projectPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.sourceIsoPath);
  return writer.toBuffer();
}

export function decodeProjectAssetRepairRequest(payload: Uint8Array): ProjectAssetRepairRequest {
  const reader = new PayloadReader(payload);
  const value = { projectPath: reader.readString(), catalogRootPath: reader.readString(), sourceIsoPath: reader.readString() };
  reader.complete();
  return value;
}

export function encodeCatalogMaintenanceRequest(value: CatalogMaintenanceRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.catalogRootPath);
  writer.writeStrings(value.projectRoots);
  return writer.toBuffer();
}

export function decodeCatalogMaintenanceRequest(payload: Uint8Array): CatalogMaintenanceRequest {
  const reader = new PayloadReader(payload);
  const value = { catalogRootPath: reader.readString(), projectRoots: reader.readStrings() };
  reader.complete();
  return value;
}

export function encodeCatalogCollectionRequest(value: CatalogCollectionRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.catalogRootPath);
  writer.writeStrings(value.projectRoots);
  writer.writeString(value.confirmationToken);
  return writer.toBuffer();
}

export function decodeCatalogCollectionRequest(payload: Uint8Array): CatalogCollectionRequest {
  const reader = new PayloadReader(payload);
  const value = {
    catalogRootPath: reader.readString(),
    projectRoots: reader.readStrings(),
    confirmationToken: reader.readString(),
  };
  reader.complete();
  return value;
}

export function encodeCatalogMaintenance(value: CatalogMaintenancePreview): Buffer {
  const writer = new PayloadWriter();
  writer.writeUInt32(value.projectCount);
  writer.writeUInt32(value.catalogAssetCount);
  writer.writeUInt32(value.protectedAssetCount);
  writer.writeUInt32(value.candidateCount);
  writer.writeUInt32(value.catalogCandidateCount);
  writer.writeUInt64(value.candidateBytes);
  writer.writeString(value.confirmationToken);
  writer.writeStrings(value.candidateKinds);
  writer.writeStrings(value.blockers);
  return writer.toBuffer();
}

export function decodeCatalogMaintenance(payload: Uint8Array): CatalogMaintenancePreview {
  const reader = new PayloadReader(payload);
  const value = {
    projectCount: reader.readUInt32(),
    catalogAssetCount: reader.readUInt32(),
    protectedAssetCount: reader.readUInt32(),
    candidateCount: reader.readUInt32(),
    catalogCandidateCount: reader.readUInt32(),
    candidateBytes: reader.readUInt64(),
    confirmationToken: reader.readString(),
    candidateKinds: reader.readStrings(),
    blockers: reader.readStrings(),
  };
  reader.complete();
  return value;
}
