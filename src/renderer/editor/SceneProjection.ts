import * as THREE from 'three';

import type { EditorEntity } from '../../types/EditorRuntime.js';

const ENTITY_ID_KEY = 'forgeEntityId';
const PS2_TO_SCENE_ROTATION = new THREE.Quaternion(-Math.SQRT1_2, 0, 0, Math.SQRT1_2);
const SCENE_TO_PS2_ROTATION = PS2_TO_SCENE_ROTATION.clone().invert();

export class SceneProjection {
  readonly root = new THREE.Group();

  private readonly geometry = new THREE.BoxGeometry(16, 16, 16);
  private readonly assetMaterial = new THREE.MeshBasicMaterial({ color: 0x4dabf7, wireframe: true });
  private readonly modelLessMaterial = new THREE.MeshBasicMaterial({ color: 0xffc078, wireframe: true });
  private readonly selectedMaterial = new THREE.MeshBasicMaterial({ color: 0xffd43b, wireframe: true });
  private readonly objects = new Map<string, THREE.Mesh>();
  private readonly pickable = new Set<string>();
  private readonly sourceRotation = new THREE.Quaternion();
  private readonly projectedRotation = new THREE.Quaternion();
  private disposed = false;

  constructor() {
    this.root.name = 'Forge project entities';
  }

  sync(entities: readonly EditorEntity[], selection: readonly string[] = []) {
    if (this.disposed) throw new Error('Scene projection is disposed');
    const retained = new Set(entities.map((entity) => entity.id));
    const selected = new Set(selection);
    this.pickable.clear();
    let created = 0;
    let updated = 0;
    let removed = 0;

    for (const [id, object] of this.objects) {
      if (retained.has(id)) continue;
      object.removeFromParent();
      this.objects.delete(id);
      removed += 1;
    }

    for (const entity of entities) {
      if (!entity.state.hidden && !entity.state.disabled && !entity.state.locked) this.pickable.add(entity.id);
      let object = this.objects.get(entity.id);
      if (!object) {
        object = new THREE.Mesh(this.geometry, this.materialFor(entity, selected.has(entity.id)));
        object.userData[ENTITY_ID_KEY] = entity.id;
        this.objects.set(entity.id, object);
        this.root.add(object);
        this.updateObject(object, entity, selected.has(entity.id));
        created += 1;
        continue;
      }
      if (this.updateObject(object, entity, selected.has(entity.id))) updated += 1;
    }

    return { created, updated, removed };
  }

  getObject(entityId: string): THREE.Object3D | undefined {
    return this.objects.get(entityId);
  }

  resolveEntityId(object: THREE.Object3D): string | undefined {
    for (let current: THREE.Object3D | null = object; current; current = current.parent) {
      const value = current.userData[ENTITY_ID_KEY];
      if (typeof value === 'string') return value;
      if (current === this.root) break;
    }
    return undefined;
  }

  resolvePick(intersections: readonly THREE.Intersection[]): string | undefined {
    for (const intersection of intersections) {
      const id = this.resolveEntityId(intersection.object);
      if (id && this.pickable.has(id)) return id;
    }
    return undefined;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.root.removeFromParent();
    this.root.clear();
    this.objects.clear();
    this.pickable.clear();
    this.geometry.dispose();
    this.assetMaterial.dispose();
    this.modelLessMaterial.dispose();
    this.selectedMaterial.dispose();
  }

  private updateObject(object: THREE.Mesh, entity: EditorEntity, selected: boolean): boolean {
    const material = this.materialFor(entity, selected);
    const visible = !entity.state.hidden && !entity.state.disabled;
    const { position, rotation, scale } = entity.transform;
    this.sourceRotation.set(rotation.x, rotation.y, rotation.z, rotation.w);
    this.projectedRotation.copy(PS2_TO_SCENE_ROTATION)
      .multiply(this.sourceRotation)
      .multiply(SCENE_TO_PS2_ROTATION);
    const changed = object.name !== entity.name
      || object.material !== material
      || object.visible !== visible
      || object.position.x !== position.x
      || object.position.y !== position.z
      || object.position.z !== -position.y
      || !object.quaternion.equals(this.projectedRotation)
      || object.scale.x !== scale.x
      || object.scale.y !== scale.z
      || object.scale.z !== scale.y;
    if (!changed) return false;

    object.name = entity.name;
    object.material = material;
    object.visible = visible;
    object.position.set(position.x, position.z, -position.y);
    object.quaternion.copy(this.projectedRotation);
    object.scale.set(scale.x, scale.z, scale.y);
    object.updateMatrix();
    return true;
  }

  private materialFor(entity: EditorEntity, selected: boolean): THREE.Material {
    return selected ? this.selectedMaterial : entity.asset ? this.assetMaterial : this.modelLessMaterial;
  }
}
