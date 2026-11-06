import { Accordion, Button, Checkbox, Group, Slider, Stack, Text } from '@mantine/core';
import { formatBytes } from '../../utils/Format.ts';
import { hasOcclusionSeedGraph, isOcclusionCoverageDisabled, needsOcclusionCoverageGeneration } from '../../utils/OcclusionCoverage.ts';
import { useEditor } from './EditorContext.ts';

export function OcclusionBuildControls() {
  const { project, coverage, busy, hostAvailable, occlusionVisibilityLoading,
    rebuildOcclusion, previewOcclusionSamples, occlusionSamples, occlusionSamplesStale,
    occlusionSamplePullIn, setOcclusionSamplePullIn, occlusionPixelEdgeAllowance, setOcclusionPixelEdgeAllowance } = useEditor();
  const authored = needsOcclusionCoverageGeneration(coverage.snapshot?.state);
  const occlusionDisabled = isOcclusionCoverageDisabled(coverage.snapshot?.state);
  const canBuild = !authored || project.capabilities.includes('editor.occlusion-coverage.build');
  const disabled = occlusionDisabled || busy || coverage.loading || coverage.previewing || occlusionVisibilityLoading || !hostAvailable || project.target.game !== 'UYA';
  const { preview } = coverage;
  const incompleteGraphs = coverage.snapshot?.state.sources.filter(s => s.enabled && hasOcclusionSeedGraph(s) && !s.graph!.markers.length) ?? [];
  return <Stack gap="sm">
    <div>
      <Text size="sm" fw={600}>Generate and build</Text>
      <Text size="xs" c="dimmed">Combines enabled volumes and enabled Primary octants, then applies exclusions.</Text>
    </div>
    <Group gap="xs">
      <Button variant="default" disabled={disabled || !coverage.previewAvailable || !coverage.snapshot || incompleteGraphs.length > 0}
        onClick={async () => { if (await coverage.startPreview()) coverage.setGraphInspection(undefined); }}>Generate octants</Button>
      <Button variant="default" disabled={disabled || !canBuild || incompleteGraphs.length > 0}
        onClick={() => void previewOcclusionSamples()}>Qualify samples</Button>
    </Group>
    <Text size="xs" c="dimmed">Generate previews coverage. Qualify saves the scene and checks sample positions. Build bakes visibility for the next map build.</Text>
    {incompleteGraphs.length > 0 && <Text size="xs" role="status">Add seeds to {incompleteGraphs.map(s => s.name).join(', ')} or disable those graphs in Volumes before generating or building.</Text>}
    {!coverage.previewAvailable && <Text size="xs" role="status">Coverage preview is unavailable in this build.</Text>}
    {!canBuild && <Text size="xs" role="status">Coverage generation is unavailable in this build. Enable Primary and disable custom volumes to build.</Text>}
    <Stack gap="sm" aria-label="Build settings">
      <Text size="xs">Sample pull-in: {occlusionSamplePullIn}</Text>
      <Slider min={0} max={5} step={1} value={occlusionSamplePullIn} onChange={setOcclusionSamplePullIn}
        disabled={disabled || !canBuild} thumbLabel="Occlusion sample pull-in"
        marks={[0, 1, 2, 3, 4, 5].map(value => ({ value, label: String(value) }))} mb="lg" />
      <Text size="xs" c="dimmed">0 keeps surface clearance; 5 pulls moved corners up to 90% toward the center, stopping at walls. Applies to qualification and rebuild.</Text>
      <Checkbox label="Pixel edge allowance (2 pixels)" checked={occlusionPixelEdgeAllowance}
        disabled={disabled || !canBuild}
        description="Includes geometry near depth edges to reduce popping. Rebuild to apply."
        onChange={event => setOcclusionPixelEdgeAllowance(event.currentTarget.checked)} />
    </Stack>
    <Button fullWidth disabled={disabled || !canBuild || incompleteGraphs.length > 0}
      onClick={() => void rebuildOcclusion()}>Build occlusion</Button>
    <Text size="xs" c="dimmed">{occlusionDisabled
      ? 'All occlusion is off. Map builds write an empty template without an occlusion bake.'
      : `${authored ? 'Build regenerates octants from your volumes.' : 'Build uses the existing Primary octants.'} Generation and qualification are optional checks before building.`}</Text>
    {preview && <Text size="xs" role="status">
      {coverage.previewStale ? 'Previous generation (out of date)' : 'Generated coverage'}: {preview.cells.length.toLocaleString()} octants.
      {' '}Inspect the result in the Viewport tab.
    </Text>}
    {occlusionSamplesStale && <Text size="xs" c="yellow" role="status">Qualified samples are out of date. Qualify again after changing the scene, coverage or pull-in.</Text>}
    {occlusionSamples && <Text size="xs" role="status">{occlusionSamples.cells.length.toLocaleString()} qualified octants · {occlusionSamples.origins.length.toLocaleString()} shared sample positions</Text>}
    {(preview || occlusionSamples?.preparation) && <Accordion variant="contained">
      <Accordion.Item value="details">
        <Accordion.Control>Generation details</Accordion.Control>
        <Accordion.Panel>
          <Stack gap="xs">
            {preview && <>
              <Text size="xs">{preview.primaryCells.toLocaleString()} Primary cells · {preview.candidateTests.toLocaleString()} candidate tests.</Text>
              {preview.graphPreview && <Text size="xs">Graph queries: {preview.graphPreview.rays.toLocaleString()} rays · {preview.graphPreview.queryTests.toLocaleString()} tests.</Text>}
            </>}
            {occlusionSamples?.preparation && <Stack gap={4} aria-label="Occlusion preparation summary">
              <Text size="xs">Budget: {occlusionSamples.cells.length.toLocaleString()} / {occlusionSamples.preparation.cellLimit.toLocaleString()} octants
                {' · '}{occlusionSamples.origins.length.toLocaleString()} / {occlusionSamples.preparation.sampleLimit.toLocaleString()} samples</Text>
              <Text size="xs">Input native grid: {formatBytes(occlusionSamples.preparation.gridBytes)}
                {' · '}tree: {formatBytes(occlusionSamples.preparation.treeBytes)}
                {' · '}{occlusionSamples.preparation.maskSlots.toLocaleString()} mask slots</Text>
              <Text size="xs">Unshared mask estimate: {formatBytes(occlusionSamples.preparation.unsharedMaskBytes)}.
                {' '}Actual packed size depends on shared masks and is measured after building.</Text>
              <Text size="xs">{occlusionSamples.preparation.alwaysVisibleGroups.toLocaleString()} always-visible fallback groups.
                {' '}Unmapped editor objects stay visible.</Text>
            </Stack>}
          </Stack>
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>}
  </Stack>;
}
