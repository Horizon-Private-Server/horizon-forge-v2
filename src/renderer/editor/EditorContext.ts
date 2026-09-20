import { createContext, useContext } from 'react';

import type { EditorSnapshot } from '../../types/EditorRuntime.js';

export const EditorContext = createContext<EditorSnapshot | undefined>(undefined);

export function useEditorSnapshot(): EditorSnapshot {
  const value = useContext(EditorContext);
  if (!value) throw new Error('Editor panel rendered outside an editor session');
  return value;
}
