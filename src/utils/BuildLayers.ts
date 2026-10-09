import type { BuildLayerId } from '../types/ForgeApi.js';

export const BUILD_LAYERS = [
  'World', 'Sky', 'Tfrags', 'Collision', 'Ties', 'Shrubs', 'Mobys', 'Gameplay', 'Lighting', 'Hud', 'Fx', 'Opaque',
] as const satisfies readonly BuildLayerId[];
