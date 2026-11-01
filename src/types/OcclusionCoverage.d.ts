import type { EditorSnapshot, ProjectTransform, ProjectVector3 } from './EditorRuntime.js';

export interface OcclusionCoverageMarker {
  id: string;
  name: string;
  position: ProjectVector3;
}

export interface OcclusionCoverageCell {
  x: number;
  y: number;
  z: number;
}

export interface OcclusionCoverageSource {
  id: string;
  name: string;
  enabled: boolean;
  role: 'include' | 'exclude';
  transform: ProjectTransform;
  boxSize?: ProjectVector3;
  cylinder?: { radius: number; height: number };
  /** size is retained only for legacy graph-only box volumes. */
  graph?: { size?: ProjectVector3; markers: OcclusionCoverageMarker[]; sticky?: boolean; stickyDistance?: number; disabled?: boolean };
}

export interface OcclusionCoverageState {
  schemaVersion: 1;
  primaryEnabled: boolean;
  sources: OcclusionCoverageSource[];
}

export interface OcclusionCoverageSnapshot {
  projectFingerprint: string;
  recipeFingerprint: string;
  /** Project identity for baked masks, excluding editor hidden/locked flags. */
  visibilityFingerprint: string;
  state: OcclusionCoverageState;
}

export interface OcclusionCoverageUpdateResult {
  project: EditorSnapshot;
  coverage: OcclusionCoverageSnapshot;
}

export interface OcclusionCoverageGraphPreview {
  sceneFingerprint: string;
  rays: number;
  queryTests: number;
  sources: { id: string; accepted: OcclusionCoverageCell[]; rejected: OcclusionCoverageCell[] }[];
}

export interface OcclusionCoveragePreview {
  projectFingerprint: string;
  recipeFingerprint: string;
  algorithmVersion: number;
  cellSize: number;
  primaryFingerprint: string;
  coordinateFingerprint: string;
  primaryCells: number;
  candidateTests: number;
  sources: { id: string; cells: number }[];
  cells: OcclusionCoverageCell[];
  graphPreview?: OcclusionCoverageGraphPreview;
}

export interface OcclusionCoverageAuthoring {
  snapshot?: OcclusionCoverageSnapshot;
  available: boolean;
  loading: boolean;
  updating: boolean;
  selectedId?: string;
  selectedMarkerId?: string;
  select(id?: string, markerId?: string): void;
  showVolumes: boolean;
  setShowVolumes(value: boolean): void;
  hiddenSources: ReadonlySet<string>;
  setSourceVisible(id: string, visible: boolean): void;
  focusRequest?: { id: string; markerId?: string; serial: number };
  focus(id: string, markerId?: string): void;
  focusHandled(): void;
  commit(state: OcclusionCoverageState, fingerprint?: string): Promise<boolean>;
  placementPosition(distance?: number): ProjectVector3 | undefined;
  setPlacementProvider(provider?: (distance?: number) => ProjectVector3 | undefined): void;
  previewAvailable: boolean;
  preview?: OcclusionCoveragePreview;
  previewing: boolean;
  previewStale: boolean;
  progress?: { completed: number; total: number };
  startPreview(): Promise<boolean>;
  cancelPreview(): void;
  showPreview: boolean;
  setShowPreview(value: boolean): void;
  graphInspection?: 'accepted' | 'rejected';
  setGraphInspection(value?: 'accepted' | 'rejected'): void;
}
