import { useCallback, useEffect, useRef, useState } from 'react';

import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import type { OcclusionCoverageAuthoring } from '../../types/OcclusionCoverage.js';
import type { OcclusionVisibilityResult } from '../../types/Occlusion.js';
import { errorMessage } from '../../utils/Errors.ts';
import { isOcclusionVisibilityCurrent, needsOcclusionCoverageGeneration } from '../../utils/OcclusionCoverage.ts';
import { useLogStatus } from '../log/UseLog.ts';

export function useOcclusionVisibility(project: Pick<EditorSnapshot, 'projectId' | 'projectPath'>,
  coverage: OcclusionCoverageAuthoring, hostAvailable: boolean, busy: boolean, bakeRevision: number) {
  const [attempt, setAttempt] = useState(0);
  const [read, setRead] = useState<{ key: string; value?: OcclusionVisibilityResult; error?: string }>();
  const [loading, setLoading] = useState(false);
  const completed = useRef<string | undefined>(undefined);
  const ready = hostAvailable && (!coverage.available || Boolean(coverage.snapshot));
  const authored = needsOcclusionCoverageGeneration(coverage.snapshot?.state);
  const key = JSON.stringify([project.projectId, project.projectPath, authored, bakeRevision, attempt]);
  const reload = useCallback(() => setAttempt(value => value + 1), []);

  useEffect(() => {
    if (!ready || busy) { setLoading(false); return; }
    if (completed.current === key) return;
    let active = true;
    setLoading(true);
    void window.forge.getOcclusionVisibility().then(value => {
      if (active) { completed.current = key; setRead({ key, value }); }
    }).catch(cause => {
      if (active) { completed.current = key; setRead({ key, error: errorMessage(cause) }); }
    }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [key, ready, busy]);

  const current = read?.key === key ? read : undefined;
  const result = current?.value;
  const data = result && !('status' in result) ? result : undefined;
  const visibility = isOcclusionVisibilityCurrent(data, coverage.snapshot) ? data : undefined;
  const warning = result && !visibility
    ? 'Visibility culling needs rebuilding for the current scene and volumes. Generated octants can be viewed without building visibility.' : undefined;
  useLogStatus('Occlusion visibility', warning);
  useLogStatus('Occlusion visibility errors', current?.error, 'error');

  return { visibility, loading, warning, error: current?.error, reload };
}
