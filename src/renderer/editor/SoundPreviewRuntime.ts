import type { SoundPreviewSource } from '../../types/SoundWorkspace.js';
import { clampSoundPreviewVolume } from '../../utils/SoundPreviewPreferences.ts';

export const SOUND_PREVIEW_CACHE_BUDGET = 32 * 1024 * 1024;

export interface SoundPreviewPlaybackState {
  nativeIndex?: number;
  playing: boolean;
  loading: boolean;
  position: number;
  duration: number;
}

interface CachedPreview {
  buffer: AudioBuffer;
  playbackRate: number;
  gain: number;
  panDegrees: number;
  isLooping: boolean;
  loopStartSeconds?: number;
  loopEndSeconds: number;
  bytes: number;
  isVariable: boolean;
}

export class SoundPreviewRuntime {
  readonly #load: (nativeIndex: number, preferAudible: boolean) => Promise<SoundPreviewSource>;
  readonly #cancelLoad: () => Promise<void>;
  readonly #onState: (state: SoundPreviewPlaybackState) => void;
  readonly #contextFactory: () => AudioContext;
  readonly #cache = new Map<string, CachedPreview>();
  readonly #pending = new Map<string, Promise<CachedPreview>>();
  #context?: AudioContext;
  #source?: AudioBufferSourceNode;
  #gain?: GainNode;
  #panner?: StereoPannerNode;
  #nativeIndex?: number;
  #cacheKey?: string;
  #position = 0;
  #startedAt = 0;
  #duration = 0;
  #generation = 0;
  #cacheBytes = 0;
  #volume = 0.75;
  #muted = false;
  #disposed = false;

  constructor(
    load: (nativeIndex: number, preferAudible: boolean) => Promise<SoundPreviewSource>,
    cancelLoad: () => Promise<void>,
    onState: (state: SoundPreviewPlaybackState) => void,
    contextFactory: () => AudioContext = () => new AudioContext(),
  ) {
    this.#load = load;
    this.#cancelLoad = cancelLoad;
    this.#onState = onState;
    this.#contextFactory = contextFactory;
  }

  get cacheBytes(): number { return this.#cacheBytes; }

  get position(): number {
    if (!this.#source || !this.#context) return this.#position;
    return Math.min(this.#duration, this.#position + this.#context.currentTime - this.#startedAt);
  }

  setDirectVolume(volume: number, muted: boolean): void {
    this.#volume = clampSoundPreviewVolume(volume);
    this.#muted = muted;
    this.#applyGain();
  }

  async play(nativeIndex: number, preferAudible = true, cacheKey = String(nativeIndex)): Promise<void> {
    if (this.#disposed) throw new Error('Sound preview is closed.');
    const generation = ++this.#generation;
    this.#stopSource(false);
    if (this.#cacheKey !== cacheKey) {
      this.#position = 0;
      this.#duration = 0;
    }
    this.#nativeIndex = nativeIndex;
    this.#cacheKey = cacheKey;
    this.#emit(true);
    try {
      for (const pendingKey of this.#pending.keys()) {
        if (pendingKey !== cacheKey) await this.#cancelLoad();
      }
      const preview = await this.#get(nativeIndex, preferAudible, cacheKey);
      if (this.#disposed || generation !== this.#generation) return;
      const context = this.#requireContext();
      await context.resume();
      if (this.#disposed || generation !== this.#generation) return;
      this.#duration = preview.buffer.duration / preview.playbackRate;
      this.#start(preview, this.#position >= this.#duration ? 0 : this.#position);
    } catch (cause) {
      if (!this.#disposed && generation === this.#generation) this.#emit(false);
      throw cause;
    }
  }

  stop(): void {
    ++this.#generation;
    void this.#cancelLoad();
    this.#stopSource(false);
    this.#position = 0;
    this.#emit(false);
  }

  seek(position: number): void {
    const next = Math.min(this.#duration, Math.max(0, Number.isFinite(position) ? position : 0));
    const index = this.#nativeIndex;
    const wasPlaying = this.#source !== undefined;
    this.#stopSource(false);
    this.#position = next;
    if (wasPlaying && index !== undefined) {
      const preview = this.#cacheKey === undefined ? undefined : this.#cache.get(this.#cacheKey);
      if (preview) this.#start(preview, next === this.#duration ? 0 : next);
    } else this.#emit(false);
  }

  async dispose(): Promise<void> {
    if (this.#disposed) return;
    this.#disposed = true;
    ++this.#generation;
    this.#stopSource(false);
    this.#pending.clear();
    this.#cache.clear();
    this.#cacheBytes = 0;
    const context = this.#context;
    this.#context = undefined;
    await Promise.allSettled([
      this.#cancelLoad(),
      context && context.state !== 'closed' ? context.close() : Promise.resolve(),
    ]);
  }

  async #get(nativeIndex: number, preferAudible: boolean, cacheKey: string): Promise<CachedPreview> {
    const cached = this.#cache.get(cacheKey);
    if (cached && !cached.isVariable) {
      this.#cache.delete(cacheKey);
      this.#cache.set(cacheKey, cached);
      return cached;
    }
    const existing = this.#pending.get(cacheKey);
    if (existing) return existing;
    const pending = this.#load(nativeIndex, preferAudible).then((source) => {
      if (this.#disposed) throw new Error('Sound preview is closed.');
      return this.#decode(source);
    });
    this.#pending.set(cacheKey, pending);
    try {
      const preview = await pending;
      if (!this.#disposed) this.#store(cacheKey, preview);
      return preview;
    } finally {
      if (this.#pending.get(cacheKey) === pending) this.#pending.delete(cacheKey);
    }
  }

  async #decode(source: SoundPreviewSource): Promise<CachedPreview> {
    const context = this.#requireContext();
    const containerRate = webAudioSampleRate(source.sampleRate);
    const buffer = await context.decodeAudioData(createSoundPreviewWave(
      source.pcm16, source.sampleRate, source.channels,
    ));
    return {
      buffer,
      playbackRate: source.sampleRate / containerRate,
      gain: source.gain,
      panDegrees: source.panDegrees,
      isLooping: source.isLooping,
      loopStartSeconds: source.loopStartFrame === undefined
        ? undefined : source.loopStartFrame / containerRate,
      loopEndSeconds: source.loopEndFrame / containerRate,
      bytes: buffer.length * buffer.numberOfChannels * Float32Array.BYTES_PER_ELEMENT,
      isVariable: source.isVariable,
    };
  }

  #store(cacheKey: string, preview: CachedPreview): void {
    const previous = this.#cache.get(cacheKey);
    if (previous) {
      this.#cache.delete(cacheKey);
      this.#cacheBytes -= previous.bytes;
    }
    if (preview.bytes > SOUND_PREVIEW_CACHE_BUDGET) return;
    while (this.#cacheBytes + preview.bytes > SOUND_PREVIEW_CACHE_BUDGET && this.#cache.size) {
      const oldest = this.#cache.entries().next().value as [string, CachedPreview];
      this.#cache.delete(oldest[0]);
      this.#cacheBytes -= oldest[1].bytes;
    }
    this.#cache.set(cacheKey, preview);
    this.#cacheBytes += preview.bytes;
  }

  #start(preview: CachedPreview, position: number): void {
    const context = this.#requireContext();
    const source = context.createBufferSource();
    const gain = context.createGain();
    source.buffer = preview.buffer;
    source.playbackRate.value = preview.playbackRate;
    source.loop = preview.isLooping;
    if (preview.isLooping) {
      source.loopStart = preview.loopStartSeconds ?? 0;
      source.loopEnd = preview.loopEndSeconds;
    }
    const pan = context.createStereoPanner();
    pan.pan.value = Math.sin(preview.panDegrees * Math.PI / 180);
    source.connect(gain).connect(pan).connect(context.destination);
    this.#source = source;
    this.#gain = gain;
    this.#panner = pan;
    this.#position = position;
    this.#startedAt = context.currentTime;
    this.#applyGain(preview.gain);
    source.onended = () => {
      if (this.#source !== source) return;
      this.#source = undefined;
      this.#gain = undefined;
      this.#panner = undefined;
      source.disconnect();
      gain.disconnect();
      pan.disconnect();
      this.#position = 0;
      this.#emit(false);
    };
    source.start(0, position * preview.playbackRate);
    this.#emit(false);
  }

  #stopSource(emit: boolean): void {
    const source = this.#source;
    const gain = this.#gain;
    const panner = this.#panner;
    this.#source = undefined;
    this.#gain = undefined;
    this.#panner = undefined;
    if (source) {
      source.onended = null;
      source.stop();
      source.disconnect();
    }
    gain?.disconnect();
    panner?.disconnect();
    if (emit) this.#emit(false);
  }

  #applyGain(sourceGain?: number): void {
    const gain = sourceGain ?? (this.#cacheKey === undefined ? 1 : this.#cache.get(this.#cacheKey)?.gain ?? 1);
    if (this.#gain) this.#gain.gain.value = this.#muted ? 0 : gain * this.#volume;
  }

  #requireContext(): AudioContext {
    return this.#context ??= this.#contextFactory();
  }

  #emit(loading: boolean): void {
    this.#onState({
      nativeIndex: this.#nativeIndex,
      playing: this.#source !== undefined,
      loading,
      position: this.position,
      duration: this.#duration,
    });
  }
}

export function createSoundPreviewWave(pcm: Uint8Array, sampleRate: number, channels: number): ArrayBuffer {
  sampleRate = webAudioSampleRate(sampleRate);
  const bytes = new Uint8Array(44 + pcm.byteLength);
  const view = new DataView(bytes.buffer);
  bytes.set(new TextEncoder().encode('RIFF'), 0);
  view.setUint32(4, 36 + pcm.byteLength, true);
  bytes.set(new TextEncoder().encode('WAVEfmt '), 8);
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * channels * 2, true);
  view.setUint16(32, channels * 2, true);
  view.setUint16(34, 16, true);
  bytes.set(new TextEncoder().encode('data'), 36);
  view.setUint32(40, pcm.byteLength, true);
  bytes.set(pcm, 44);
  return bytes.buffer;
}

function webAudioSampleRate(sampleRate: number): number {
  return Math.max(8_000, sampleRate);
}
