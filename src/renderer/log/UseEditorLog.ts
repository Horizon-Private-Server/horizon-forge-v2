import { useEffect, useRef } from 'react';

import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import type { EditorLoadProgress } from '../../types/ForgeApi.js';
import { forgeLog } from './LogStore.ts';
import { useLogStatus } from './UseLog.ts';

export function useEditorLog(project: EditorSnapshot, sceneLoad?: EditorLoadProgress) {
  const projectId = useRef<string | undefined>(undefined);
  const sceneLoading = useRef(false);
  useEffect(() => {
    if (projectId.current !== project.projectId) {
      forgeLog.sync('Project diagnostics', []);
      projectId.current = project.projectId;
    }
    forgeLog.sync('Project diagnostics', [
      ...project.diagnostics.map((value) => ({ ...value, source: project.projectName })),
      ...(project.baseLevel.missingAssetCount ? [{ source: project.projectName, severity: 'warning' as const,
        code: 'base.assets.missing', message: `${project.baseLevel.missingAssetCount} source instances were unavailable `
          + 'when this project was created. Re-import the source catalog and recreate the project to include them.' }] : []),
    ]);
  }, [project]);

  useLogStatus('Scene', sceneLoad?.label, sceneLoad?.status === 'error' ? 'error' : 'info');
  useEffect(() => {
    if (!sceneLoad && sceneLoading.current) forgeLog.write('Scene', 'success', 'Scene loaded.');
    sceneLoading.current = sceneLoad?.status === 'loading';
  }, [sceneLoad]);
}
