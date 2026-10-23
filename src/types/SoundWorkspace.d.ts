export interface SoundSampleMetadata {
  assetId: string;
  sourceContentId: string;
  encodedLength: number;
  decodedFrameCount: number;
  isLooping: boolean;
  loopStartFrame?: number;
  loopEndFrame: number;
  sampleRate?: number;
  channels: number;
}

export interface SoundMobyDependent {
  classId: number;
  mobyAssetId?: string;
  remapSlots: number[];
}

export interface SoundProgramEntry {
  assetId?: string;
  sourceContentId?: string;
  nativeIndex: number;
  origin: string;
  sourceGame: string;
  sourceLevel: number;
  bankSha256: string;
  importBatchId?: string;
  volumeGroup: number;
  globalRemapSlots: number[];
  mobyDependents: SoundMobyDependent[];
  samples: SoundSampleMetadata[];
  hasRandomTriggerGate: boolean;
  previewSupported: boolean;
  diagnostic?: string;
}

export interface SoundWorkspace {
  programs: SoundProgramEntry[];
  sampleCount: number;
  mobyRemapCount: number;
  globalRemapCount: number;
}

export interface SoundPreviewSource {
  nativeIndex: number;
  assetId: string;
  sampleId: string;
  sampleRate: number;
  channels: number;
  pcm16: Uint8Array;
  isLooping: boolean;
  loopStartFrame?: number;
  loopEndFrame: number;
  gain: number;
  panDegrees: number;
  isVariable: boolean;
}
