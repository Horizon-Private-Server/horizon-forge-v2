import { createContext, useContext } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type {
  EditorCollisionFaceType, EditorCommand, EditorSnapshot, EditorInstancedCollisionCandidate, EditorInstancedCollisionGenerationSettings,
  EditorInstancedCollisionPreview,
  EditorInstancedCollisionSourceInfo,
} from '../../types/EditorRuntime.js';
import type { AssetExplorerFamily } from '../../types/AssetExplorer.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
import type { CollisionVisualization } from '../../types/CollisionVisualization.js';
import type {
  BuildLayerId,
  BuildPatchProgress,
  BuildPatchResult,
  EditorLoadProgress,
  EditorTerrainSource,
} from '../../types/ForgeApi.js';

export interface EditorContextValue {
  project: EditorSnapshot;
  keybindings: KeybindingMap;
  selectionColor: string;
  sceneTreeColors: SceneTreeColors;
  collisionVisualization: CollisionVisualization;
  terrain?: EditorTerrainSource;
  sceneLoad?: EditorLoadProgress;
  setSceneLoad(progress?: EditorLoadProgress): void;
  cameraFocus?: { entityId: string };
  setCameraFocus(request?: { entityId: string }): void;
  splinePointSelection: string[];
  setSplinePointSelection(values: string[]): void;
  skyCompositionSelected: boolean;
  setSkyCompositionSelected(value: boolean): void;
  showViewportStats: boolean;
  showOcclusionOctants: boolean;
  setShowOcclusionOctants(value: boolean): void;
  showTerrain: boolean;
  setShowTerrain(value: boolean): void;
  showSolidCollision: boolean;
  setShowSolidCollision(value: boolean): void;
  showPlayerBarriers: boolean;
  setShowPlayerBarriers(value: boolean): void;
  assetPreview?: AssetExplorerFamily;
  inspectAsset(family: AssetExplorerFamily, activate?: boolean): void;
  inspectReferencedAsset(assetId: string, assetKind: string): Promise<void>;
  navigateToEntity(entityId: string, action: 'select' | 'reveal' | 'focus'): Promise<void>;
  showReferences(): void;
  busy: boolean;
  hostAvailable: boolean;
  buildProgress?: BuildPatchProgress;
  buildResult?: BuildPatchResult;
  build(includedLayers: BuildLayerId[]): Promise<void>;
  cancelBuild(): Promise<void>;
  execute(command: EditorCommand): Promise<boolean>;
  inspectInstancedCollisionSource(entityId: string): Promise<EditorInstancedCollisionSourceInfo>;
  previewInstancedCollision(
    entityId: string,
    settings?: EditorInstancedCollisionGenerationSettings,
  ): Promise<EditorInstancedCollisionPreview>;
  cancelInstancedCollisionPreview(): Promise<void>;
  applyInstancedCollisionPreview(token: string): Promise<boolean>;
  instancedCollisionOverlay?: InstancedCollisionOverlay;
  setInstancedCollisionOverlay(value?: InstancedCollisionOverlay): void;
  renderedInstancedCollisionEntityIds: ReadonlySet<string>;
  setInstancedCollisionRendered(entityId: string, rendered: boolean): void;
  save(): Promise<void>;
}

export interface InstancedCollisionOverlay {
  entityId: string;
  candidate?: EditorInstancedCollisionCandidate;
  url: string;
  showSource: boolean;
  showProxy: boolean;
  wireframe: boolean;
  showOctants: boolean;
  paint?: {
    active: boolean;
    proxyAssetId: string;
    defaultRawType: number;
    faceTypes: EditorCollisionFaceType[];
    brushRawType: number;
    interaction: 'paint' | 'reset' | 'eyedropper';
    onHover(faceId?: number, rawType?: number): void;
    onStroke(faceIds: number[], rawType: number): Promise<boolean>;
    onEyedropper(rawType: number): void;
  };
}

export const EditorContext = createContext<EditorContextValue | undefined>(undefined);

export function useEditor(): EditorContextValue {
  const value = useContext(EditorContext);
  if (!value) throw new Error('Editor panel rendered outside an editor session');
  return value;
}

export function useEditorSnapshot(): EditorSnapshot {
  return useEditor().project;
}
