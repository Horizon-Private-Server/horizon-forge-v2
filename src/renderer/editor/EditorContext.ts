import { createContext, useContext } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type {
  EditorCommand, EditorSnapshot, EditorTieCollisionCandidate, EditorTieCollisionGenerationSettings,
  EditorTieCollisionPreview,
  EditorTieCollisionSourceInfo,
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
  busy: boolean;
  hostAvailable: boolean;
  buildProgress?: BuildPatchProgress;
  buildResult?: BuildPatchResult;
  build(includedLayers: BuildLayerId[]): Promise<void>;
  cancelBuild(): Promise<void>;
  execute(command: EditorCommand): Promise<boolean>;
  inspectTieCollisionSource(entityId: string): Promise<EditorTieCollisionSourceInfo>;
  previewTieCollision(
    entityId: string,
    settings?: EditorTieCollisionGenerationSettings,
  ): Promise<EditorTieCollisionPreview>;
  cancelTieCollisionPreview(): Promise<void>;
  applyTieCollisionPreview(token: string): Promise<boolean>;
  tieCollisionOverlay?: TieCollisionOverlay;
  setTieCollisionOverlay(value?: TieCollisionOverlay): void;
  save(): Promise<void>;
}

export interface TieCollisionOverlay {
  entityId: string;
  candidate: EditorTieCollisionCandidate;
  url: string;
  showSource: boolean;
  showProxy: boolean;
  wireframe: boolean;
  showOctants: boolean;
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
