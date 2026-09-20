import { createContext, useContext } from 'react';

import type { EditorCommand, EditorSnapshot } from '../../types/EditorRuntime.js';
import type { EditorTerrainSource } from '../../types/ForgeApi.js';

export interface EditorContextValue {
  project: EditorSnapshot;
  terrain?: EditorTerrainSource;
  terrainStatus: string;
  busy: boolean;
  execute(command: EditorCommand): Promise<void>;
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
