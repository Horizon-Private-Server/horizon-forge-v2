import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';

import type { OcclusionViewportState } from '../../types/Occlusion.js';

const OcclusionViewportContext = createContext<{
  state: OcclusionViewportState;
  setState(value: OcclusionViewportState): void;
} | undefined>(undefined);

export function OcclusionViewportProvider({ projectId, children }: { projectId: string; children: ReactNode }) {
  const [state, setState] = useState<OcclusionViewportState>({ hiddenEntities: 0 });
  useEffect(() => { setState({ hiddenEntities: 0 }); }, [projectId]);
  const value = useMemo(() => ({ state, setState }), [state]);
  return <OcclusionViewportContext.Provider value={value}>{children}</OcclusionViewportContext.Provider>;
}

export function useOcclusionViewport() {
  const value = useContext(OcclusionViewportContext);
  if (!value) throw new Error('Occlusion panel rendered outside an editor session');
  return value;
}
