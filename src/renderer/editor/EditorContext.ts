import { createContext, useContext } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type { EditorCommand, EditorSnapshot } from '../../types/EditorRuntime.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
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
  sceneTreeColors: SceneTreeColors;
  terrain?: EditorTerrainSource;
  sceneLoad?: EditorLoadProgress;
  setSceneLoad(progress?: EditorLoadProgress): void;
  skyPieces: readonly string[];
  setSkyPieces(pieces: string[]): void;
  cameraFocus?: { entityId: string };
  setCameraFocus(request?: { entityId: string }): void;
  showViewportStats: boolean;
  showOcclusionOctants: boolean;
  setShowOcclusionOctants(value: boolean): void;
  busy: boolean;
  hostAvailable: boolean;
  buildProgress?: BuildPatchProgress;
  buildResult?: BuildPatchResult;
  build(includedLayers: BuildLayerId[]): Promise<void>;
  cancelBuild(): Promise<void>;
  execute(command: EditorCommand): Promise<boolean>;
  save(): Promise<void>;
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
