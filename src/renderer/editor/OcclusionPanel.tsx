import { Button, Checkbox, Fieldset, Group, Progress, Slider, Stack, Text } from '@mantine/core';

import { useEditor } from './EditorContext.ts';
import { useOcclusionViewport } from './OcclusionViewportContext.tsx';
import { EditorPanel } from './EditorPrimitives.tsx';

export function OcclusionPanel() {
  const { project, terrain, busy, hostAvailable, buildProgress, rebuildOcclusion, previewOcclusionSamples,
    occlusionSamples, occlusionSamplesStale, occlusionSamplePullIn, setOcclusionSamplePullIn,
    occlusionPixelEdgeAllowance, setOcclusionPixelEdgeAllowance, showOcclusionSamples, setShowOcclusionSamples,
    showOcclusionOctants, setShowOcclusionOctants, showOcclusionOctantIds, setShowOcclusionOctantIds,
    occlusionEnabled, setOcclusionEnabled, occlusionFrozen, setOcclusionFrozen, occlusionVisibility,
    occlusionVisibilityLoading, occlusionVisibilityError, cancelBuild } = useEditor();
  const { state: occlusionViewportState } = useOcclusionViewport();
  const active = occlusionViewportState.octant;
  return <EditorPanel label="Occlusion">
    <Stack gap="md">
      <Fieldset legend="Viewport visibility">
        <Stack gap="xs">
          <Checkbox label="Enable occlusion in Forge" checked={occlusionEnabled}
            disabled={busy || occlusionVisibilityLoading || !hostAvailable || project.target.game !== 'UYA'}
            description="Uses the latest baked masks, or vanilla masks before the first bake. Outside all octants, everything is visible."
            onChange={event => setOcclusionEnabled(event.currentTarget.checked)} />
          <Checkbox label="Freeze current octant" checked={occlusionFrozen}
            disabled={!occlusionEnabled || occlusionVisibilityLoading || !active && !occlusionFrozen}
            description="Hold this octant’s visibility while flying elsewhere."
            onChange={event => setOcclusionFrozen(event.currentTarget.checked)} />
          {occlusionVisibilityLoading && <Text size="xs" role="status">Loading occlusion masks…</Text>}
          {occlusionVisibilityError && <Text size="xs" c="red" role="alert">{occlusionVisibilityError}</Text>}
          {occlusionEnabled && occlusionVisibility && <Text size="xs" role="status">
            {occlusionFrozen ? 'Frozen' : 'Current'}: {active ? `#${active.id} (${active.x}, ${active.y}, ${active.z})` : 'No octant — everything visible'}
            {' · '}{occlusionVisibility.source === 'generated' ? 'Baked' : 'Vanilla'} masks
            {' · '}{occlusionViewportState.hiddenEntities.toLocaleString()} objects culled
          </Text>}
          <Text size="xs" c="dimmed">Visibility uses the last saved mask and object mappings. Unmapped objects stay visible.</Text>
        </Stack>
      </Fieldset>
      <Fieldset legend="Octant display">
        <Stack gap="xs">
          <Checkbox checked={showOcclusionOctants}
            label={`Show occlusion octants (${(terrain?.occlusionOctants.length ?? 0).toLocaleString()})`}
            onChange={event => setShowOcclusionOctants(event.currentTarget.checked)} />
          <Checkbox checked={showOcclusionOctantIds}
            label="Show octant IDs" description="Label nearby octants independently of cube visibility. The frozen octant stays labeled at any distance."
            onChange={event => setShowOcclusionOctantIds(event.currentTarget.checked)} />
        </Stack>
      </Fieldset>
      <Fieldset legend="Bake and samples">
        <Stack gap="xs">
        <Text size="sm" mb={4}>Sample pull-in: {occlusionSamplePullIn}</Text>
        <Slider min={0} max={5} step={1} value={occlusionSamplePullIn} onChange={setOcclusionSamplePullIn}
          disabled={busy || occlusionVisibilityLoading || !hostAvailable || project.target.game !== 'UYA'} thumbLabel="Occlusion sample pull-in"
          marks={[0, 1, 2, 3, 4, 5].map(value => ({ value, label: String(value) }))} mb="xl" />
        <Text size="xs" c="dimmed" mb="xs">0 keeps surface clearance; 5 pulls moved corners up to 90% toward the center, stopping at walls. Inward checks keep repairs in nearby open regions. Applies to qualification and rebuild.</Text>
        <Checkbox label="Pixel edge allowance (2 pixels)" checked={occlusionPixelEdgeAllowance}
          disabled={busy || occlusionVisibilityLoading || !hostAvailable || project.target.game !== 'UYA'} mb="xs"
          description="Includes geometry near depth edges to reduce popping. Rebuild occlusion to apply."
          onChange={event => setOcclusionPixelEdgeAllowance(event.currentTarget.checked)} />
        <Group grow gap="xs" mb="xs">
          <Button variant="default" disabled={busy || occlusionVisibilityLoading || !hostAvailable || project.target.game !== 'UYA'}
            onClick={() => void previewOcclusionSamples()}>Qualify samples</Button>
          <Button variant="default" disabled={busy || occlusionVisibilityLoading || !hostAvailable || project.target.game !== 'UYA'}
            onClick={() => void rebuildOcclusion()}>Rebuild occlusion</Button>
        </Group>
        <Text size="xs" c="dimmed" mb="xs">Qualify samples saves the scene and finds sample positions around walls without baking visibility.</Text>
        <Text size="xs" c="dimmed" mb="xs">Rebuild visibility for the existing octants using the current scene. The next build includes the result.</Text>
        {occlusionSamplesStale && <Text size="xs" c="yellow" mb="xs" role="status">Qualified samples are out of date. Qualify samples again after changing the scene or pull-in.</Text>}
        {occlusionSamples && <Stack gap={4} mb="xs">
          <Checkbox checked={showOcclusionSamples} label="Show qualified samples"
            onChange={event => setShowOcclusionSamples(event.currentTarget.checked)} />
          <Text size="xs" c="dimmed">Squares: centers · dots: unchanged corners · diamonds and lines: moved corners · rings: extra vertical-edge midpoints in octants with moved corners. Samples are visible through geometry.</Text>
          <Text size="xs" c="dimmed">Pink outlines mark {occlusionSamples.cells.filter(cell => cell.inwardCorners).length.toLocaleString()} octants using inward repairs. Pink crosses and paths show the affected samples.</Text>
        </Stack>}
        </Stack>
      </Fieldset>
      {(busy || occlusionVisibilityLoading) && <>
        {buildProgress && <><Text size="xs">{buildProgress.message}</Text><Progress value={buildProgress.total ? buildProgress.completed / buildProgress.total * 100 : 0} size="xs" /></>}
        <Button variant="default" onClick={() => { if (occlusionVisibilityLoading) setOcclusionEnabled(false); void cancelBuild(); }}>Cancel</Button>
      </>}
      {!busy && !occlusionVisibilityLoading && buildProgress?.phase === 'Complete' && <Text size="xs" role="status">{buildProgress.message}</Text>}
    </Stack>
  </EditorPanel>;
}
