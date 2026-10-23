import type * as THREE from 'three';

import type {
  EditorEntityKind,
  EditorReference,
  ProjectQuaternion,
  ProjectVector3,
} from './EditorRuntime.js';

export interface MobySimulationTransformSnapshot {
  readonly position: Readonly<ProjectVector3>;
  readonly rotation: Readonly<ProjectQuaternion>;
  readonly scale: Readonly<ProjectVector3>;
}

export interface MobySimulationEntitySnapshot {
  readonly id: string;
  readonly classId: number;
  readonly transform: MobySimulationTransformSnapshot;
  readonly pvar?: Readonly<Uint8Array>;
  readonly pvarFields: readonly MobySimulationPVarFieldSnapshot[];
  readonly references: readonly Readonly<EditorReference>[];
  readonly hidden: boolean;
  readonly disabled: boolean;
  readonly assetId?: string;
}

export interface MobySimulationPVarFieldSnapshot {
  readonly path: string;
  readonly invalid: boolean;
  readonly value?: MobySimulationPVarValue;
  readonly targetEntityId?: string;
}

export type MobySimulationPVarValue =
  | { readonly kind: 'integer'; readonly value: string }
  | { readonly kind: 'float'; readonly value: number }
  | { readonly kind: 'boolean'; readonly value: boolean }
  | { readonly kind: 'color'; readonly value: readonly number[] }
  | { readonly kind: 'vector'; readonly value: readonly number[] }
  | { readonly kind: 'bytes'; readonly value: string }
  | { readonly kind: 'reference'; readonly value?: string };

export interface MobySimulationReferenceTargetSnapshot {
  readonly id: string;
  readonly kind: EditorEntityKind;
  readonly transform: MobySimulationTransformSnapshot;
  readonly hidden: boolean;
  readonly disabled: boolean;
}

export interface MobySimulationProjectSnapshot {
  readonly projectId: string;
  readonly revision: number;
  readonly game: string;
  readonly entities: readonly MobySimulationEntitySnapshot[];
  readonly referenceTargets: readonly MobySimulationReferenceTargetSnapshot[];
}

export type MobySimulationInvalidationKind =
  | 'transform'
  | 'pvar'
  | 'reference'
  | 'asset'
  | 'fx'
  | 'visibility';

export interface MobySimulationInvalidation {
  readonly kind: MobySimulationInvalidationKind;
  readonly entityIds: readonly string[];
  readonly dependencyIds?: readonly string[];
}

export interface MobySimulationFixedUpdate {
  readonly stepSeconds: number;
  readonly tick: number;
  readonly timeSeconds: number;
}

export interface MobySimulationRenderUpdate {
  readonly timeSeconds: number;
  readonly interpolationAlpha: number;
}

export interface MobySimulationResource<T> {
  readonly value: T;
  release(): void;
}

export interface MobySimulationResolvedAsset {
  readonly id: string;
  readonly revision: string;
  readonly url: string;
}

export interface MobySimulationResolvedFxTexture extends MobySimulationResolvedAsset {
  readonly fxId: number;
}

export interface MobySimulationDiagnostic {
  readonly code: string;
  readonly severity: 'warning' | 'error';
  readonly entityIds: readonly string[];
  readonly message: string;
}

export interface MobySimulationRendererServices {
  readonly root: THREE.Object3D;
  readonly camera: THREE.Camera;
  acquireAsset(assetId: string): Promise<MobySimulationResource<MobySimulationResolvedAsset> | undefined>;
  acquireFxTexture(fxId: number): Promise<MobySimulationResource<MobySimulationResolvedFxTexture> | undefined>;
  setProxyVisible(entityId: string, visible: boolean): void;
  bindSelection(object: THREE.Object3D, entityId: string): () => void;
  reportDiagnostic(diagnostic: MobySimulationDiagnostic): void;
}

export interface MobySimulationFactoryContext {
  readonly snapshot: MobySimulationProjectSnapshot;
  readonly entities: readonly MobySimulationEntitySnapshot[];
  readonly services: MobySimulationRendererServices;
}

export interface MobySimulationSelectionObject {
  readonly object: THREE.Object3D;
  readonly entityId: string;
}

export interface MobySimulation {
  readonly entityIds: readonly string[];
  readonly selectionObjects?: readonly MobySimulationSelectionObject[];
  enable(): void;
  fixedUpdate(update: MobySimulationFixedUpdate): void;
  renderUpdate(update: MobySimulationRenderUpdate): void;
  reset(): void;
  disable(): void;
  invalidate(
    context: MobySimulationFactoryContext,
    invalidation: MobySimulationInvalidation,
  ): 'handled' | 'recreate';
  dispose(): void;
}

export type MobySimulationFactory = (
  context: MobySimulationFactoryContext,
) => MobySimulation | null | Promise<MobySimulation | null>;

export interface MobySimulationStats {
  readonly registeredFactories: number;
  readonly activeSimulators: number;
  readonly simulatedEntities: number;
  readonly fixedTicks: number;
  readonly droppedSeconds: number;
  readonly hiddenProxies: number;
  readonly selectionBindings: number;
}
