import assert from 'node:assert/strict';
import test from 'node:test';

import {
  decodeSoundPreview, decodeSoundWorkspace, encodeSoundPreview,
  encodeSoundPreviewRequest, encodeSoundWorkspace,
} from '../src/main/bridge/SoundPayloadCodec.ts';
import {
  createSoundPreviewWave, SOUND_PREVIEW_CACHE_BUDGET, SoundPreviewRuntime,
} from '../src/renderer/editor/SoundPreviewRuntime.ts';
import type { SoundPreviewSource, SoundWorkspace } from '../src/types/SoundWorkspace.js';
import { soundSlotsForMoby } from '../src/utils/SoundWorkspace.ts';

test('sound workspace and PCM preview bridge payloads round trip', () => {
  const workspace: SoundWorkspace = {
    programs: [{
      assetId: 'a'.repeat(64), sourceContentId: 'b'.repeat(64), nativeIndex: 7, origin: 'Base',
      sourceGame: 'UYA', sourceLevel: 3, bankSha256: 'c'.repeat(64), volumeGroup: -2,
      importBatchId: undefined, globalRemapSlots: [1, 4],
      mobyDependents: [{ classId: 0x222, mobyAssetId: undefined, remapSlots: [0, 2] }],
      samples: [{
        assetId: 'd'.repeat(64), sourceContentId: 'e'.repeat(64), encodedLength: 16,
        decodedFrameCount: 28, isLooping: true, loopStartFrame: 3, loopEndFrame: 27,
        sampleRate: 22_050, channels: 1,
      }],
      hasRandomTriggerGate: true, previewSupported: false, diagnostic: 'unsupported branch grain',
    }],
    sampleCount: 1, mobyRemapCount: 1, globalRemapCount: 2,
  };
  assert.deepEqual(decodeSoundWorkspace(encodeSoundWorkspace(workspace)), workspace);
  assert.deepEqual([...encodeSoundPreviewRequest(7, true)], [
    7, 0, 0, 0, 1,
    0, 0, 0, 0,
    0, 0, 0, 0,
    0, 0, 0, 0,
  ]);
  assert.ok(encodeSoundPreviewRequest(7, true, {
    catalogRootPath: '/assets', bankAssetId: 'a'.repeat(64), game: 'UYA',
  }).length > 17);

  const preview = previewSource(7, 8, 2);
  const decoded = decodeSoundPreview(encodeSoundPreview(preview));
  assert.deepEqual({ ...decoded, pcm16: [...decoded.pcm16] }, {
    ...preview, pcm16: [...preview.pcm16],
  });
});

test('moby sound slots are deduplicated and ordered by remap slot', () => {
  const program = (nativeIndex: number, classId: number, remapSlots: number[]) => ({
    nativeIndex, origin: 'Base', sourceGame: 'UYA', sourceLevel: 3, bankSha256: 'c'.repeat(64),
    volumeGroup: 0, globalRemapSlots: [],
    mobyDependents: [{ classId, mobyAssetId: undefined, remapSlots }], samples: [],
    hasRandomTriggerGate: false, previewSupported: true,
  } satisfies SoundWorkspace['programs'][number]);
  const slots = soundSlotsForMoby([
    program(9, 0x222, [3, 1, 1]),
    program(4, 0x222, [1]),
    program(2, 0x333, [0]),
  ], 0x222);
  assert.deepEqual(slots.map(({ slot, program: entry }) => [slot, entry.nativeIndex]), [
    [1, 4], [1, 9], [3, 9],
  ]);
});

test('preview runtime deduplicates decode, applies volume, seeks, and releases audio state', async () => {
  const context = new FakeAudioContext();
  let loads = 0;
  let state = { playing: false, loading: false, position: 0, duration: 0 };
  const runtime = new SoundPreviewRuntime(
    async (nativeIndex) => { loads++; return previewSource(nativeIndex, 8_000); },
    async () => {},
    (value) => { state = value; },
    () => context as unknown as AudioContext,
  );
  await Promise.all([runtime.play(4), runtime.play(4)]);
  assert.equal(loads, 1);
  assert.equal(state.playing, true);
  assert.equal(runtime.cacheBytes, 8_000 * 4);
  runtime.setDirectVolume(0.5, false);
  assert.equal(context.gains.at(-1)?.gain.value, 0.25);
  runtime.seek(0.25);
  assert.equal(context.sources.at(-1)?.offset, 0.25);
  runtime.stop();
  assert.equal(context.sources.every((value) => value.stopped), true);
  await runtime.dispose();
  assert.equal(runtime.cacheBytes, 0);
  assert.equal(context.state, 'closed');
  assert.equal(context.nodes.every((value) => value.disconnected), true);
});

test('rapid preview selection cancels stale work and only starts the latest sound', async () => {
  const context = new FakeAudioContext();
  const pending = new Map<number, (value: SoundPreviewSource) => void>();
  let cancellations = 0;
  let latestIndex: number | undefined;
  const runtime = new SoundPreviewRuntime(
    (nativeIndex) => new Promise((resolve) => pending.set(nativeIndex, resolve)),
    async () => { cancellations++; },
    (value) => { latestIndex = value.nativeIndex; },
    () => context as unknown as AudioContext,
  );
  const first = runtime.play(1);
  const second = runtime.play(2);
  await Promise.resolve();
  pending.get(2)!(previewSource(2, 8));
  pending.get(1)!(previewSource(1, 8));
  await Promise.all([first, second]);
  assert.equal(cancellations, 1);
  assert.equal(latestIndex, 2);
  assert.equal(context.sources.length, 1);
  await runtime.dispose();
});

test('preview cache separates equal native indexes from different source banks', async () => {
  const context = new FakeAudioContext();
  let loads = 0;
  const runtime = new SoundPreviewRuntime(
    async (nativeIndex) => { loads++; return previewSource(nativeIndex, 8); },
    async () => {},
    () => {},
    () => context as unknown as AudioContext,
  );
  await runtime.play(66, true, 'bank-a:66');
  await runtime.play(66, true, 'bank-b:66');
  await runtime.play(66, true, 'bank-a:66');
  assert.equal(loads, 2);
  await runtime.dispose();
});

test('decoded preview cache remains within its measured memory budget', async () => {
  const context = new FakeAudioContext();
  const runtime = new SoundPreviewRuntime(
    async (nativeIndex) => previewSource(nativeIndex, 2_000_000),
    async () => {},
    () => {},
    () => context as unknown as AudioContext,
  );
  for (let index = 0; index < 5; index++) await runtime.play(index);
  assert.equal(runtime.cacheBytes <= SOUND_PREVIEW_CACHE_BUDGET, true);
  assert.equal(runtime.cacheBytes > SOUND_PREVIEW_CACHE_BUDGET - 8_000_000, true);
  await runtime.dispose();
});

test('variable previews reload while retaining their current buffer for seeking', async () => {
  const context = new FakeAudioContext();
  let loads = 0;
  const runtime = new SoundPreviewRuntime(
    async (nativeIndex) => {
      loads++;
      return { ...previewSource(nativeIndex, 8_000), isVariable: true };
    },
    async () => {},
    () => {},
    () => context as unknown as AudioContext,
  );
  await runtime.play(4);
  const cachedBytes = runtime.cacheBytes;
  runtime.seek(0.25);
  await runtime.play(4);
  assert.equal(context.sources.at(-2)?.offset, 0.25);
  assert.equal(loads, 2);
  assert.equal(context.sources.length, 3);
  assert.equal(runtime.cacheBytes, cachedBytes);
  await runtime.dispose();
});

test('loop points retain source timing when Web Audio resamples the decoded buffer', async () => {
  const context = new FakeAudioContext(48_000);
  const runtime = new SoundPreviewRuntime(
    async (nativeIndex) => ({
      ...previewSource(nativeIndex, 8_000),
      isLooping: true,
      loopStartFrame: 80,
      loopEndFrame: 4_000,
    }),
    async () => {},
    () => {},
    () => context as unknown as AudioContext,
  );
  await runtime.play(4);
  assert.equal(context.sources[0].buffer?.sampleRate, 48_000);
  assert.equal(context.sources[0].loopStart, 0.01);
  assert.equal(context.sources[0].loopEnd, 0.5);
  await runtime.dispose();
});

test('sub-8 kHz previews use a compatible WAV rate and compensate with playback rate', async () => {
  const sourceFrames = 2_352;
  const sourceRate = 1_938;
  const wave = createSoundPreviewWave(new Uint8Array(sourceFrames * 2), sourceRate, 1);
  const header = new DataView(wave);
  assert.equal(header.getUint32(24, true), 8_000);
  assert.equal(header.getUint32(40, true) / 2, sourceFrames);

  const context = new FakeAudioContext();
  let duration = 0;
  const runtime = new SoundPreviewRuntime(
    async () => ({ ...previewSource(156, sourceFrames), sampleRate: sourceRate }),
    async () => {},
    (state) => { duration = state.duration; },
    () => context as unknown as AudioContext,
  );
  await runtime.play(156);
  assert.equal(context.sources[0].playbackRate.value, sourceRate / 8_000);
  assert.equal(Math.abs(duration - sourceFrames / sourceRate) < 0.001, true);
  runtime.seek(0.25);
  assert.equal(context.sources.at(-1)?.offset, 0.25 * sourceRate / 8_000);
  await runtime.dispose();
});

test('disposing during a load cannot recreate audio state or retain the late buffer', async () => {
  let resolve!: (value: SoundPreviewSource) => void;
  let contexts = 0;
  const runtime = new SoundPreviewRuntime(
    () => new Promise((complete) => { resolve = complete; }),
    async () => {},
    () => {},
    () => { contexts++; return new FakeAudioContext() as unknown as AudioContext; },
  );
  const playing = runtime.play(9);
  await runtime.dispose();
  resolve(previewSource(9, 8));
  await assert.rejects(playing, /closed/);
  assert.equal(contexts, 0);
  assert.equal(runtime.cacheBytes, 0);
});

function previewSource(nativeIndex: number, frames: number, channels = 1): SoundPreviewSource {
  return {
    nativeIndex, assetId: 'a'.repeat(64), sampleId: 'b'.repeat(64), sampleRate: 8_000,
    channels, pcm16: new Uint8Array(frames * channels * 2), isLooping: false,
    loopStartFrame: undefined, loopEndFrame: frames,
    gain: 0.5, panDegrees: 0, isVariable: false,
  };
}

class FakeNode {
  disconnected = false;
  connect<T>(node: T): T { return node; }
  disconnect(): void { this.disconnected = true; }
}

class FakeSource extends FakeNode {
  buffer: FakeAudioBuffer | null = null;
  loop = false;
  loopStart = 0;
  loopEnd = 0;
  playbackRate = { value: 1 };
  onended: (() => void) | null = null;
  offset = 0;
  stopped = false;
  start(_when: number, offset: number): void { this.offset = offset; }
  stop(): void { this.stopped = true; }
}

class FakeAudioBuffer {
  readonly data: Float32Array;
  readonly numberOfChannels: number;
  readonly length: number;
  readonly sampleRate: number;
  constructor(length: number, sampleRate: number, channels = 1) {
    this.length = length;
    this.sampleRate = sampleRate;
    this.numberOfChannels = channels;
    this.data = new Float32Array(length);
  }
  get duration(): number { return this.length / this.sampleRate; }
  getChannelData(): Float32Array { return this.data; }
}

class FakeGain extends FakeNode { gain = { value: 1 }; }
class FakePanner extends FakeNode { pan = { value: 0 }; }

class FakeAudioContext {
  readonly decodedSampleRate?: number;
  currentTime = 0;
  state: AudioContextState = 'suspended';
  destination = new FakeNode();
  sources: FakeSource[] = [];
  gains: FakeGain[] = [];
  panners: FakePanner[] = [];
  constructor(decodedSampleRate?: number) { this.decodedSampleRate = decodedSampleRate; }
  get nodes(): FakeNode[] { return [...this.sources, ...this.gains, ...this.panners]; }
  createBuffer(_channels: number, frames: number, rate: number): FakeAudioBuffer {
    return new FakeAudioBuffer(frames, rate);
  }
  async decodeAudioData(bytes: ArrayBuffer): Promise<FakeAudioBuffer> {
    const view = new DataView(bytes);
    const channels = view.getUint16(22, true);
    const sourceRate = view.getUint32(24, true);
    const sourceFrames = view.getUint32(40, true) / 2 / channels;
    const sampleRate = this.decodedSampleRate ?? sourceRate;
    return new FakeAudioBuffer(
      Math.round(sourceFrames * sampleRate / sourceRate), sampleRate, channels,
    );
  }
  createBufferSource(): FakeSource { const value = new FakeSource(); this.sources.push(value); return value; }
  createGain(): FakeGain { const value = new FakeGain(); this.gains.push(value); return value; }
  createStereoPanner(): FakePanner { const value = new FakePanner(); this.panners.push(value); return value; }
  async resume(): Promise<void> { this.state = 'running'; }
  async close(): Promise<void> { this.state = 'closed'; }
}
