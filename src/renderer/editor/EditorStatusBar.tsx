import { Button, Group, Progress, Text } from '@mantine/core';

import type { BuildPatchProgress, BuildPatchResult, ForgeHostStatus } from '../../types/ForgeApi.js';
import { useEditorSnapshot } from './EditorContext.ts';

export function EditorStatusBar({ hostStatus, busy, buildProgress, buildResult, onCancel }: {
  hostStatus?: ForgeHostStatus;
  busy: boolean;
  buildProgress?: BuildPatchProgress;
  buildResult?: BuildPatchResult;
  onCancel(): void;
}) {
  const project = useEditorSnapshot();
  const layers = new Set(project.entities.map((entity) => entity.layer));
  const dirtyLayers = new Set(project.entities.filter((entity) => entity.state.dirty).map((entity) => entity.layer));
  const warnings = project.diagnostics.filter((value) => value.severity !== 'info').length
    + (project.baseLevel.missingAssetCount ? 1 : 0)
    + (project.migrationPending ? 1 : 0);
  return <Group className="editor-status-bar" role="status" wrap="nowrap">
    <Text>{hostStatus ? 'Host connected' : 'Host unavailable'}</Text>
    <Text>{project.target.game} {project.target.region} · Level {project.baseLevel.level}</Text>
    <Text>{project.selection.length} selected</Text>
    <Text>{project.entities.length} entities · {layers.size} layers</Text>
    <Text>{dirtyLayers.size ? `${dirtyLayers.size} changed layers` : 'Layers unchanged'}</Text>
    <Text>{project.isDirty ? 'Bake: changes pending' : 'Bake: unchanged'}</Text>
    <Text>{busy
      ? `${buildProgress?.phase ?? 'Build'}: ${buildProgress?.message ?? 'Working…'}`
      : buildResult?.succeeded ? buildResult.message : 'Task: idle'}</Text>
    {busy && buildProgress && <Progress
      aria-label="Build progress"
      value={buildProgress.total ? buildProgress.completed / buildProgress.total * 100 : 0}
      w={120}
      size="xs"
    />}
    <Text c={warnings ? 'yellow' : 'dimmed'}>{warnings} warnings/errors</Text>
    {busy && <Button ml="auto" variant="subtle" onClick={onCancel}>Cancel</Button>}
    <Button ml={busy ? undefined : 'auto'} variant="subtle" onClick={() => void window.forge.revealLogs()}>Logs</Button>
  </Group>;
}
