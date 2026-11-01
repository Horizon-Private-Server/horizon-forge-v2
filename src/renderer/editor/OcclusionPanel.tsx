import { Button, Group, Progress, Stack, Tabs, Text } from '@mantine/core';
import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye';
import { HammerIcon } from '@phosphor-icons/react/dist/csr/Hammer';
import { TreeStructureIcon } from '@phosphor-icons/react/dist/csr/TreeStructure';
import { useEditor } from './EditorContext.ts';
import { EditorPanel } from './EditorPrimitives.tsx';
import { OcclusionCoverageControls } from './OcclusionCoverageControls.tsx';
import { OcclusionBuildControls } from './OcclusionBuildControls.tsx';
import { OcclusionViewportControls } from './OcclusionViewportControls.tsx';

export function OcclusionPanel() {
  const { coverage, busy, buildProgress, occlusionVisibilityLoading, setOcclusionEnabled, cancelBuild } = useEditor();
  return <EditorPanel label="Occlusion">
    <Stack gap="sm">
      {coverage.previewing && <Stack gap={4} aria-label="Coverage generation progress">
        <Group justify="space-between" gap="xs">
          <Text size="xs" role="status">Generating octants…</Text>
          <Button variant="default" onClick={coverage.cancelPreview}>Cancel generation</Button>
        </Group>
        <Progress size="xs" value={coverage.progress?.total ? coverage.progress.completed / coverage.progress.total * 100 : 0} aria-label="Coverage generation" />
      </Stack>}
      {(busy && !coverage.updating || occlusionVisibilityLoading) && <Stack gap={4} aria-label="Occlusion operation progress">
        <Group justify="space-between" gap="xs">
          <Text size="xs" role="status">{buildProgress?.message ?? (occlusionVisibilityLoading ? 'Loading occlusion masks…' : 'Working…')}</Text>
          <Button variant="default" onClick={() => { if (occlusionVisibilityLoading) setOcclusionEnabled(false); void cancelBuild(); }}>Cancel</Button>
        </Group>
        {buildProgress && <Progress value={buildProgress.total ? buildProgress.completed / buildProgress.total * 100 : 0} size="xs" />}
      </Stack>}
      {(!busy || coverage.updating) && !occlusionVisibilityLoading && buildProgress?.phase === 'Complete' && <Text size="xs" role="status">{buildProgress.message}</Text>}
      <Tabs defaultValue="sources" keepMounted>
        <Tabs.List grow aria-label="Occlusion tasks">
          <Tabs.Tab value="sources" leftSection={<TreeStructureIcon size={14} />}>Volumes</Tabs.Tab>
          <Tabs.Tab value="build" leftSection={<HammerIcon size={14} />}>Build</Tabs.Tab>
          <Tabs.Tab value="viewport" leftSection={<EyeIcon size={14} />}>Viewport</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="sources" pt="sm"><OcclusionCoverageControls /></Tabs.Panel>
        <Tabs.Panel value="build" pt="sm"><OcclusionBuildControls /></Tabs.Panel>
        <Tabs.Panel value="viewport" pt="sm"><OcclusionViewportControls /></Tabs.Panel>
      </Tabs>
    </Stack>
  </EditorPanel>;
}
