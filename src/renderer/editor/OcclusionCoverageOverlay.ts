import * as THREE from 'three';
import type { ProjectTransform } from '../../types/EditorRuntime.js';
import type { EditorTransformTarget } from '../../types/EditorViewport.js';
import type { OcclusionCoverageAuthoring, OcclusionCoveragePreview, OcclusionCoverageSource } from '../../types/OcclusionCoverage.js';
import { projectPositionToLocal, projectTransformToSceneMatrix, sceneMatrixToProjectTransform } from '../../utils/Transforms.ts';
import { DEFAULT_SCENE_TREE_COLORS } from '../../utils/SceneTreeColors.ts';
import { OcclusionOctantOverlay } from './OcclusionOctantOverlay.ts';
import { isOcclusionCoverageState, transformOcclusionCoverageSource } from '../../utils/OcclusionCoverage.ts';

interface Volume {
  source: OcclusionCoverageSource;
  signature: string;
  root: THREE.Group;
  mesh: THREE.Mesh;
  lines: THREE.LineSegments;
  markers?: THREE.Points;
  markerSource?: OcclusionCoverageSource['graph'];
  markerSelection?: string;
}

export class OcclusionCoverageOverlay {
  readonly root = new THREE.Group();
  private readonly box = new THREE.BoxGeometry(1, 1, 1);
  private readonly cylinder = new THREE.CylinderGeometry(1, 1, 1, 32);
  private readonly boxEdges = new THREE.EdgesGeometry(this.box);
  private readonly cylinderEdges = new THREE.EdgesGeometry(this.cylinder);
  private readonly pickerMaterial = new THREE.MeshBasicMaterial({ visible: false, side: THREE.DoubleSide });
  private readonly volumes = new Map<string, Volume>();
  private readonly selectedBounds = new THREE.Box3Helper(new THREE.Box3(), '#ffffff');
  readonly octants = new THREE.Group();
  private previewData?: OcclusionCoveragePreview;
  private previewInspection?: string;
  private authoring?: OcclusionCoverageAuthoring;
  private disposed = false;

  constructor() {
    this.root.name = 'Coverage authoring helpers'; this.root.add(this.selectedBounds); this.octants.name = 'Coverage octant preview';
    this.selectedBounds.visible = false; this.selectedBounds.raycast = () => {};
    this.selectedBounds.renderOrder = 1000;
    Object.assign(this.selectedBounds.material, { transparent: true, depthTest: false, depthWrite: false, fog: false, toneMapped: false });
  }

  sync(authoring?: OcclusionCoverageAuthoring, octantColor = DEFAULT_SCENE_TREE_COLORS.occlusionOctant): void {
    this.authoring = authoring;
    const sources = authoring?.snapshot?.state.sources ?? [];
    const ids = new Set(sources.map(s => s.id));
    for (const [id, volume] of this.volumes) if (!ids.has(id)) { this.disposeVolume(volume); this.volumes.delete(id); }
    for (const source of sources) {
      let volume = this.volumes.get(source.id);
      if (volume && Boolean(volume.source.cylinder) !== Boolean(source.cylinder)) {
        this.disposeVolume(volume); this.volumes.delete(source.id); volume = undefined;
      }
      if (!volume) {
        const geometry = source.cylinder ? this.cylinder : this.box;
        const edges = source.cylinder ? this.cylinderEdges : this.boxEdges;
        const material = new THREE.LineDashedMaterial({ depthTest: false, depthWrite: false, fog: false,
          toneMapped: false, transparent: true, dashSize: .12, gapSize: .08 });
        const mesh = new THREE.Mesh(geometry, this.pickerMaterial);
        const lines = new THREE.LineSegments(edges, material); lines.computeLineDistances(); lines.raycast = () => {};
        const root = new THREE.Group(); root.matrixAutoUpdate = false; root.add(mesh, lines); this.root.add(root);
        volume = { source, signature: '', root, mesh, lines }; this.volumes.set(source.id, volume);
      }
      const signature = JSON.stringify(source);
      if (signature !== volume.signature) {
        volume.source = source; volume.signature = signature; this.applySource(volume, source);
      }
      volume.root.visible = Boolean(authoring?.showVolumes && !authoring.hiddenSources.has(source.id));
      const material = volume.lines.material as THREE.LineDashedMaterial;
      material.color.set(source.role === 'exclude' ? '#ffa84d' : '#55ddff');
      material.opacity = source.enabled ? 1 : .3;
      material.gapSize = source.role === 'exclude' ? .08 : 0;
      this.updateMarkerSelection(volume);
    }
    this.updateSelectedBounds(); this.syncPreview(authoring, octantColor);
  }

  private applySource(volume: Volume, source: OcclusionCoverageSource): void {
    projectTransformToSceneMatrix(source.transform, volume.root.matrix);
    const size = source.boxSize ?? source.graph?.size;
    const scale = size ? new THREE.Vector3(size.x, size.z, size.y)
      : new THREE.Vector3(source.cylinder!.radius, source.cylinder!.height, source.cylinder!.radius);
    volume.mesh.scale.copy(scale); volume.lines.scale.copy(scale);
    if (volume.markerSource?.markers !== source.graph?.markers) {
      volume.markerSource = source.graph;
      if (volume.markers && (!source.graph || volume.markers.geometry.getAttribute('position').count !== source.graph.markers.length)) {
        volume.markers.geometry.dispose(); (volume.markers.material as THREE.Material).dispose(); volume.markers.removeFromParent(); volume.markers = undefined;
      }
      if (source.graph) {
        if (!volume.markers) {
          const geometry = new THREE.BufferGeometry();
          geometry.setAttribute('position', new THREE.Float32BufferAttribute(source.graph.markers.length * 3, 3));
          volume.markers = new THREE.Points(geometry, new THREE.PointsMaterial({ size: 10, sizeAttenuation: false,
            vertexColors: true, toneMapped: false, depthTest: false, depthWrite: false, fog: false }));
          volume.markers.raycast = () => {}; volume.root.add(volume.markers);
        }
        const positions = volume.markers.geometry.getAttribute('position') as THREE.BufferAttribute;
        source.graph.markers.forEach((m, i) => positions.setXYZ(i, m.position.x, m.position.z, -m.position.y));
        positions.needsUpdate = true; volume.markers.geometry.computeBoundingSphere();
        volume.markerSelection = undefined; this.updateMarkerSelection(volume);
      }
    }
    volume.root.updateMatrixWorld(true);
    this.updateSelectedBounds();
  }

  private updateMarkerSelection(volume: Volume): void {
    const selected = this.authoring?.selectedId === volume.source.id ? this.authoring?.selectedMarkerId ?? '' : '';
    if (!volume.markers || volume.markerSelection === selected) return;
    volume.markerSelection = selected;
    const markers = volume.markerSource!.markers;
    let colors = volume.markers.geometry.getAttribute('color') as THREE.BufferAttribute | undefined;
    if (!colors || colors.count !== markers.length) {
      colors = new THREE.Float32BufferAttribute(markers.length * 3, 3); volume.markers.geometry.setAttribute('color', colors);
    }
    markers.forEach((marker, i) => colors!.setXYZ(i, 1, marker.id === selected ? .65 : 1, marker.id === selected ? .2 : 1));
    colors.needsUpdate = true;
  }

  pickMarker(pointer: THREE.Vector2, camera: THREE.Camera, width: number, height: number): { sourceId: string; markerId: string } | undefined {
    if (!this.authoring?.showVolumes) return undefined;
    this.root.updateWorldMatrix(true, true);
    const point = new THREE.Vector3(); let closest = 8 * 8, depth = Infinity;
    let result: { sourceId: string; markerId: string } | undefined;
    for (const volume of this.volumes.values()) {
      if (!volume.root.visible) continue;
      for (const marker of volume.markerSource?.markers ?? []) {
        point.set(marker.position.x, marker.position.z, -marker.position.y).applyMatrix4(volume.root.matrixWorld).project(camera);
        if (point.z < -1 || point.z > 1) continue;
        const distance = ((point.x - pointer.x) * width / 2) ** 2 + ((point.y - pointer.y) * height / 2) ** 2;
        if (distance < closest || distance === closest && point.z < depth) {
          closest = distance; depth = point.z; result = { sourceId: volume.source.id, markerId: marker.id };
        }
      }
    }
    return result;
  }

  getBounds(id: string, target = new THREE.Box3(), markerId?: string): THREE.Box3 | undefined {
    const volume = this.volumes.get(id); if (!volume) return undefined;
    volume.root.updateWorldMatrix(true, true);
    if (markerId) {
      const marker = volume.markerSource?.markers.find(m => m.id === markerId); if (!marker) return undefined;
      const point = new THREE.Vector3(marker.position.x, marker.position.z, -marker.position.y).applyMatrix4(volume.root.matrixWorld);
      return target.setFromCenterAndSize(point, new THREE.Vector3(1, 1, 1));
    }
    const geometry = volume.mesh.geometry; if (!geometry.boundingBox) geometry.computeBoundingBox();
    return target.copy(geometry.boundingBox!).applyMatrix4(volume.mesh.matrixWorld);
  }

  pick(raycaster: THREE.Raycaster): string | undefined {
    if (!this.authoring?.showVolumes) return undefined;
    const visible = [...this.volumes.entries()].filter(([, volume]) => volume.root.visible);
    this.root.updateWorldMatrix(true, true);
    const hit = raycaster.intersectObjects(visible.map(([, volume]) => volume.mesh), false)[0];
    return hit && visible.find(([, volume]) => volume.mesh === hit.object)?.[0];
  }

  target(): EditorTransformTarget | undefined {
    const authoring = this.authoring, snapshot = authoring?.snapshot;
    const id = authoring?.selectedId, volume = id ? this.volumes.get(id) : undefined;
    if (!snapshot || !id || !volume?.root.visible || authoring?.loading) return undefined;
    const source = volume.source, markerId = authoring.selectedMarkerId;
    const marker = markerId ? source.graph?.markers.find(m => m.id === markerId) : undefined;
    if (markerId && !marker) return undefined;
    const markerMatrix = marker ? projectTransformToSceneMatrix(source.transform).multiply(
      new THREE.Matrix4().makeTranslation(marker.position.x, marker.position.z, -marker.position.y)) : undefined;
    const transformSource = (transform: ProjectTransform, axis?: string | null): OcclusionCoverageSource | undefined => {
      if (!marker) return transformOcclusionCoverageSource(source, transform, axis);
      const updated = { ...marker, position: projectPositionToLocal(source.transform, transform.position) };
      const value = { ...source, graph: { ...source.graph!, markers: source.graph!.markers.map(m => m.id === marker.id ? updated : m) } };
      return isOcclusionCoverageState({ schemaVersion: 1, primaryEnabled: false, sources: [value] }) ? value : undefined;
    };
    return {
      key: `${snapshot.projectFingerprint}:${id}:${markerId ?? ''}`, translateOnly: Boolean(marker),
      transform: markerMatrix ? sceneMatrixToProjectTransform(markerMatrix) : source.transform,
      vertices: () => {
        if (markerMatrix) return [new THREE.Vector3().setFromMatrixPosition(markerMatrix)];
        const positions = volume.mesh.geometry.getAttribute('position'); volume.root.updateWorldMatrix(true, true);
        return Array.from({ length: positions.count }, (_, i) => new THREE.Vector3().fromBufferAttribute(positions, i).applyMatrix4(volume.mesh.matrixWorld));
      },
      preview: (transform, axis) => {
        if (this.disposed || this.authoring?.snapshot?.projectFingerprint !== snapshot.projectFingerprint) return false;
        const value = transform ? transformSource(transform, axis) : source;
        this.applySource(volume, value ?? source); return Boolean(value);
      },
      commit: (transform, axis) => {
        const value = transformSource(transform, axis); if (!value) return;
        void authoring.commit({ ...snapshot.state, sources: snapshot.state.sources.map(s => s.id === id ? value : s) }, snapshot.projectFingerprint)
          .finally(() => {
            if (!this.disposed && this.authoring?.snapshot?.projectFingerprint === snapshot.projectFingerprint) this.applySource(volume, source);
          });
      },
    };
  }

  private updateSelectedBounds(): void {
    const id = this.authoring?.selectedId, volume = id ? this.volumes.get(id) : undefined;
    this.selectedBounds.visible = Boolean(volume?.root.visible);
    if (volume && this.selectedBounds.visible) this.getBounds(id!, this.selectedBounds.box, this.authoring?.selectedMarkerId);
  }

  private syncPreview(authoring: OcclusionCoverageAuthoring | undefined, octantColor: string): void {
    const preview = authoring?.preview;
    const inspection = authoring?.graphInspection;
    const key = inspection ? `${inspection}:${authoring?.selectedId ?? ''}` : 'effective';
    if (preview !== this.previewData || key !== this.previewInspection) {
      this.disposePreview(); this.previewData = preview; this.previewInspection = key;
    }
    this.octants.visible = Boolean(preview && authoring?.showPreview);
    if (!this.octants.visible || !preview) return;
    const graph = preview.graphPreview?.sources.find(s => s.id === authoring?.selectedId);
    const cells = inspection ? graph?.[inspection] ?? [] : preview.cells;
    if (!this.octants.children.length && cells.length) {
      this.octants.add(new OcclusionOctantOverlay(cells, octantColor, preview.cellSize));
    }
    const overlay = this.octants.children[0] as OcclusionOctantOverlay | undefined;
    overlay?.setColor(authoring?.previewStale ? '#ffb34d'
      : inspection === 'rejected' ? '#ff83a5' : inspection === 'accepted' ? '#70dcff' : octantColor);
  }

  private disposePreview(): void {
    for (const overlay of [...this.octants.children]) (overlay as OcclusionOctantOverlay).dispose();
  }

  private disposeVolume(volume: Volume): void {
    (volume.lines.material as THREE.Material).dispose();
    if (volume.markers) { volume.markers.geometry.dispose(); (volume.markers.material as THREE.Material).dispose(); }
    volume.root.removeFromParent(); volume.root.clear();
  }

  dispose(): void {
    this.disposed = true; this.disposePreview(); this.octants.removeFromParent();
    for (const volume of this.volumes.values()) this.disposeVolume(volume);
    this.volumes.clear(); this.box.dispose(); this.cylinder.dispose(); this.boxEdges.dispose(); this.cylinderEdges.dispose();
    this.pickerMaterial.dispose(); this.selectedBounds.geometry.dispose(); (this.selectedBounds.material as THREE.Material).dispose();
    this.root.removeFromParent(); this.root.clear();
  }
}
