import * as THREE from 'three';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import { ps2PositionToScene } from '../../utils/Scene.ts';

interface TfragTemplateResult {
  matched: number;
  missingAssetIds: string[];
}

export function tfragSourceIndex(url: string): number | undefined {
  const path = decodeURIComponent(new URL(url).pathname);
  const chunk = path.match(/\/tfrag\/chunks\/chunk(\d+)\/tfrag\.gltf$/i)?.[1];
  if (chunk !== undefined) return Number(chunk);
  return /\/tfrag\/tfrag\.gltf$/i.test(path) ? 0 : undefined;
}

export function addTfragTemplates(
  scene: THREE.Object3D,
  url: string,
  entities: readonly EditorEntity[],
  templates: Map<string, THREE.Object3D>,
): TfragTemplateResult {
  const sourceIndex = tfragSourceIndex(url);
  if (sourceIndex === undefined) return { matched: 0, missingAssetIds: [] };
  const sourceGroups = new Map<string, THREE.Object3D>();
  scene.traverse((object) => {
    if (/^chunk_\d{4}_lod_0$/i.test(object.name)) sourceGroups.set(object.name.toLowerCase(), object);
  });
  let matched = 0;
  const missingAssetIds = new Set<string>();
  for (const entity of entities) {
    if (!entity.asset || entity.tfrag?.sourceIndex !== sourceIndex) continue;
    if (templates.has(entity.asset.id)) {
      matched += 1;
      continue;
    }
    const source = sourceGroups.get(`chunk_${String(entity.tfrag.groupIndex).padStart(4, '0')}_lod_0`);
    if (!source) {
      missingAssetIds.add(entity.asset.id);
      continue;
    }
    const template = new THREE.Group();
    template.name = `Tfrag asset ${entity.asset.id}`;
    template.position.copy(ps2PositionToScene(
      entity.tfrag.sourcePosition ?? entity.transform.position,
      new THREE.Vector3(),
    )).multiplyScalar(-1);
    template.add(source.clone(true));
    template.updateMatrixWorld(true);
    templates.set(entity.asset.id, template);
    matched += 1;
  }
  return { matched, missingAssetIds: [...missingAssetIds] };
}
