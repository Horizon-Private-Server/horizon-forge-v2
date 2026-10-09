export type SceneBillboardGlyph = 'object' | 'robot' | 'area' | 'ship' | 'sphere' | 'settings';

export interface SceneBillboardOptions {
  entityId: string;
  label: string;
  worldPosition: { x: number; y: number; z: number };
  color: string | number;
  glyph: SceneBillboardGlyph;
  depthPolicy: 'occluded' | 'overlay';
  selected: boolean;
  hovered: boolean;
  worldSize: number;
  minScreenSize: number;
  maxScreenSize: number;
}

export interface SceneBillboardResourceCounts {
  geometries: number;
  materials: number;
  textures: number;
}
