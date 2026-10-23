import * as THREE from 'three';

export interface UyaOceanWaveComponent {
  readonly amplitude: number;
  readonly waveVector: THREE.Vector2;
  readonly angularSpeed: number;
  readonly phase: number;
}

const DIRECTION_SLOTS = [0, 2, 1, 3, 6, 4, 5, 7] as const;
const SIZE_BLEND_STEP = 0.29166666;
const TAU = Math.PI * 2;

export function createUyaOceanWaves(settings: {
  speed: number;
  crest: number;
  surge: number;
  rippleSize: number;
  directionDegrees: number;
  directionVariation: number;
  shimmerIntensity: number;
}): UyaOceanWaveComponent[] {
  const speed = finite(settings.speed);
  const amplitudeScale = finite(settings.crest);
  const longWavelength = finite(settings.surge);
  const shortWavelength = finite(settings.rippleSize);
  if (amplitudeScale === 0 || longWavelength <= 0 || shortWavelength <= 0) return [];
  const geometricMean = Math.sqrt(longWavelength * shortWavelength);
  const sizeBlend = finite(settings.shimmerIntensity);
  const wavelengths = DIRECTION_SLOTS.map((_, index) => index < 4
    ? shortWavelength + (geometricMean - shortWavelength) * sizeBlend * index * SIZE_BLEND_STEP
    : longWavelength + (geometricMean - longWavelength) * sizeBlend * (7 - index) * SIZE_BLEND_STEP);
  if (wavelengths.some((value) => !Number.isFinite(value) || value <= 0)) return [];
  const acceleration = speed * TAU * speed * 2 / (shortWavelength + wavelengths[3]);
  return wavelengths.map((wavelength, index) => {
    const direction = THREE.MathUtils.degToRad(
      finite(settings.directionDegrees) + finite(settings.directionVariation) * 45 * (DIRECTION_SLOTS[index] - 3.5),
    );
    return {
      amplitude: wavelength * amplitudeScale,
      waveVector: new THREE.Vector2(Math.cos(direction) * TAU / wavelength, Math.sin(direction) * TAU / wavelength),
      angularSpeed: -Math.sqrt(Math.max(0, acceleration * TAU / wavelength)),
      phase: index * Math.PI * 0.25,
    };
  });
}

export function uyaOceanWaveSlopeNormalization(waves: readonly UyaOceanWaveComponent[]): number {
  let x = 0;
  let y = 0;
  for (const wave of waves) {
    x += wave.amplitude * Math.abs(wave.waveVector.x);
    y += wave.amplitude * Math.abs(wave.waveVector.y);
  }
  return Math.hypot(x, y);
}

function finite(value: number): number {
  return Number.isFinite(value) ? value : 0;
}
