import { Buffer } from 'node:buffer';

import type { SoundPreviewSource, SoundWorkspace } from '../../types/SoundWorkspace.js';
import { malformed, PayloadReader, PayloadWriter } from './PayloadIO.ts';

const MAX_PROGRAMS = 32_767;
const MAX_PCM_BYTES = 8_000_000;

export function encodeSoundPreviewRequest(
  nativeIndex: number,
  preferAudible: boolean,
  source?: { catalogRootPath: string; bankAssetId: string; game: string },
): Buffer {
  if (!Number.isInteger(nativeIndex) || nativeIndex < 0 || nativeIndex >= MAX_PROGRAMS)
    malformed('Sound program index is invalid');
  const writer = new PayloadWriter();
  writer.writeInt32(nativeIndex);
  writer.writeBoolean(preferAudible);
  writer.writeString(source?.catalogRootPath ?? '');
  writer.writeString(source?.bankAssetId ?? '');
  writer.writeString(source?.game ?? '');
  return writer.toBuffer();
}

export function encodeSoundWorkspace(value: SoundWorkspace): Buffer {
  if (value.programs.length > MAX_PROGRAMS) malformed('Sound program list exceeds limit');
  const writer = new PayloadWriter();
  writer.writeUInt32(value.programs.length);
  value.programs.forEach((program) => {
    writer.writeString(program.assetId ?? '');
    writer.writeString(program.sourceContentId ?? '');
    writer.writeInt32(program.nativeIndex);
    writer.writeString(program.origin);
    writer.writeString(program.sourceGame);
    writer.writeInt32(program.sourceLevel);
    writer.writeString(program.bankSha256);
    writer.writeString(program.importBatchId ?? '');
    writer.writeInt32(program.volumeGroup);
    writeInts(writer, program.globalRemapSlots);
    writer.writeUInt32(program.mobyDependents.length);
    program.mobyDependents.forEach((dependent) => {
      writer.writeInt32(dependent.classId);
      writer.writeString(dependent.mobyAssetId ?? '');
      writeInts(writer, dependent.remapSlots);
    });
    writer.writeUInt32(program.samples.length);
    program.samples.forEach((sample) => {
      writer.writeString(sample.assetId);
      writer.writeString(sample.sourceContentId);
      writer.writeInt32(sample.encodedLength);
      writer.writeInt32(sample.decodedFrameCount);
      writer.writeBoolean(sample.isLooping);
      writer.writeInt32(sample.loopStartFrame ?? -1);
      writer.writeInt32(sample.loopEndFrame);
      writer.writeInt32(sample.sampleRate ?? 0);
      writer.writeInt32(sample.channels);
    });
    writer.writeBoolean(program.hasRandomTriggerGate);
    writer.writeBoolean(program.previewSupported);
    writer.writeString(program.diagnostic ?? '');
  });
  writer.writeInt32(value.sampleCount);
  writer.writeInt32(value.mobyRemapCount);
  writer.writeInt32(value.globalRemapCount);
  return writer.toBuffer();
}

export function encodeSoundPreview(value: SoundPreviewSource): Buffer {
  const writer = new PayloadWriter();
  writer.writeInt32(value.nativeIndex);
  writer.writeString(value.assetId);
  writer.writeString(value.sampleId);
  writer.writeInt32(value.sampleRate);
  writer.writeInt32(value.channels);
  writer.writeBytes(value.pcm16, MAX_PCM_BYTES);
  writer.writeBoolean(value.isLooping);
  writer.writeInt32(value.loopStartFrame ?? -1);
  writer.writeInt32(value.loopEndFrame);
  writer.writeFloat32(value.gain);
  writer.writeInt32(value.panDegrees);
  writer.writeBoolean(value.isVariable);
  return writer.toBuffer();
}

export function decodeSoundWorkspace(payload: Uint8Array): SoundWorkspace {
  const reader = new PayloadReader(payload);
  const count = reader.readUInt32();
  if (count > MAX_PROGRAMS) malformed('Sound program list exceeds limit');
  const programs = Array.from({ length: count }, () => {
    const assetId = optional(reader.readString());
    const sourceContentId = optional(reader.readString());
    const nativeIndex = reader.readInt32();
    const origin = reader.readString();
    const sourceGame = reader.readString();
    const sourceLevel = reader.readInt32();
    const bankSha256 = reader.readString();
    const importBatchId = optional(reader.readString());
    const volumeGroup = reader.readInt32();
    const globalRemapSlots = readInts(reader);
    const dependentCount = reader.readUInt32();
    if (dependentCount > MAX_PROGRAMS) malformed('Sound dependent list exceeds limit');
    const mobyDependents = Array.from({ length: dependentCount }, () => ({
      classId: reader.readInt32(),
      mobyAssetId: optional(reader.readString()),
      remapSlots: readInts(reader),
    }));
    const sampleCount = reader.readUInt32();
    if (sampleCount > MAX_PROGRAMS) malformed('Sound sample list exceeds limit');
    const samples = Array.from({ length: sampleCount }, () => {
      const sample = {
        assetId: reader.readString(),
        sourceContentId: reader.readString(),
        encodedLength: reader.readInt32(),
        decodedFrameCount: reader.readInt32(),
        isLooping: reader.readBoolean(),
        loopStartFrame: optionalIndex(reader.readInt32()),
        loopEndFrame: reader.readInt32(),
        sampleRate: optionalPositive(reader.readInt32()),
        channels: reader.readInt32(),
      };
      if (!isHash(sample.assetId) || !isHash(sample.sourceContentId)
        || sample.encodedLength < 0 || sample.decodedFrameCount < 0
        || sample.loopEndFrame < 0 || sample.loopEndFrame > sample.decodedFrameCount
        || sample.loopStartFrame !== undefined && (sample.loopStartFrame > sample.loopEndFrame || !sample.isLooping)
        || sample.sampleRate !== undefined && sample.sampleRate < 1
        || sample.channels !== 1) malformed('Sound sample metadata is invalid');
      return sample;
    });
    const hasRandomTriggerGate = reader.readBoolean();
    const previewSupported = reader.readBoolean();
    const diagnostic = optional(reader.readString());
    if (assetId !== undefined && !isHash(assetId) || sourceContentId !== undefined && !isHash(sourceContentId)
      || !isHash(bankSha256) || nativeIndex < 0 || sourceLevel < 0 || !sourceGame
      || volumeGroup < -128 || volumeGroup > 127 || globalRemapSlots.some((value) => value < 0)
      || mobyDependents.some((value) => value.classId < 0 || value.classId > 0xffff
        || value.mobyAssetId !== undefined && !isHash(value.mobyAssetId)
        || value.remapSlots.some((slot) => slot < 0))) malformed('Sound program metadata is invalid');
    return {
      assetId, sourceContentId, nativeIndex, origin, sourceGame, sourceLevel, bankSha256, importBatchId,
      volumeGroup, globalRemapSlots, mobyDependents, samples,
      hasRandomTriggerGate, previewSupported, diagnostic,
    };
  });
  const value = {
    programs,
    sampleCount: reader.readInt32(),
    mobyRemapCount: reader.readInt32(),
    globalRemapCount: reader.readInt32(),
  };
  if (value.sampleCount < 0 || value.mobyRemapCount < 0 || value.globalRemapCount < 0)
    malformed('Sound workspace counts are invalid');
  reader.complete();
  return value;
}

export function decodeSoundPreview(payload: Uint8Array): SoundPreviewSource {
  const reader = new PayloadReader(payload);
  const value = {
    nativeIndex: reader.readInt32(),
    assetId: reader.readString(),
    sampleId: reader.readString(),
    sampleRate: reader.readInt32(),
    channels: reader.readInt32(),
    pcm16: reader.readBytes(MAX_PCM_BYTES),
    isLooping: reader.readBoolean(),
    loopStartFrame: optionalIndex(reader.readInt32()),
    loopEndFrame: reader.readInt32(),
    gain: reader.readFloat32(),
    panDegrees: reader.readInt32(),
    isVariable: reader.readBoolean(),
  };
  reader.complete();
  const frames = value.pcm16.byteLength / 2 / value.channels;
  if (value.nativeIndex < 0 || !isHash(value.assetId) || !isHash(value.sampleId)
    || value.sampleRate < 1_000 || value.sampleRate > 192_000 || ![1, 2].includes(value.channels)
    || value.pcm16.byteLength === 0 || value.pcm16.byteLength % (2 * value.channels) !== 0
    || value.loopEndFrame < 0 || value.loopEndFrame > frames
    || value.loopStartFrame !== undefined && (value.loopStartFrame > value.loopEndFrame || !value.isLooping)
    || !Number.isFinite(value.gain) || value.gain < 0 || value.gain > 1
    || value.panDegrees < 0 || value.panDegrees >= 360) malformed('Sound preview is invalid');
  return value;
}

function readInts(reader: PayloadReader): number[] {
  const count = reader.readUInt32();
  if (count > MAX_PROGRAMS) malformed('Sound integer list exceeds limit');
  return Array.from({ length: count }, () => reader.readInt32());
}

function writeInts(writer: PayloadWriter, values: number[]): void {
  if (values.length > MAX_PROGRAMS) malformed('Sound integer list exceeds limit');
  writer.writeUInt32(values.length);
  values.forEach((value) => writer.writeInt32(value));
}

function optional(value: string): string | undefined {
  return value || undefined;
}

function optionalIndex(value: number): number | undefined {
  return value < 0 ? undefined : value;
}

function optionalPositive(value: number): number | undefined {
  return value <= 0 ? undefined : value;
}

function isHash(value: string): boolean {
  return /^[0-9a-f]{64}$/.test(value);
}
