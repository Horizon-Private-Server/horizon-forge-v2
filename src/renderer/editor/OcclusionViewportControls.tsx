import { Accordion, Button, Checkbox, Fieldset, Select, Stack, Text } from '@mantine/core';
import { needsOcclusionCoverageGeneration } from '../../utils/OcclusionCoverage.ts';
import { useEditor } from './EditorContext.ts';
import { useOcclusionViewport } from './OcclusionViewportContext.tsx';

export function OcclusionViewportControls() {
  const { project, terrain, coverage, busy, hostAvailable, occlusionSamples, occlusionSamplesStale,
    showOcclusionSamples, setShowOcclusionSamples, showOcclusionOctants, setShowOcclusionOctants,
    showOcclusionOctantIds, setShowOcclusionOctantIds, occlusionEnabled, setOcclusionEnabled,
    occlusionFrozen, setOcclusionFrozen, occlusionVisibility, occlusionVisibilityLoading,
    occlusionVisibilityError, occlusionVisibilityWarning, reloadOcclusionVisibility } = useEditor();
  const { state } = useOcclusionViewport();
  const primaryEnabled = coverage.snapshot?.state.primaryEnabled !== false;
  const authored = needsOcclusionCoverageGeneration(coverage.snapshot?.state);
  const builtCoverage = Boolean(occlusionVisibility?.coverageRecipeFingerprint);
  const visibilityAvailable = !authored || builtCoverage;
  const displayAvailable = primaryEnabled || builtCoverage || Boolean(occlusionSamples);
  const octantCount = occlusionSamples?.cells.length ?? (builtCoverage ? occlusionVisibility!.octants.length : primaryEnabled ? terrain?.occlusionOctants.length ?? 0 : 0);
  const source = coverage.snapshot?.state.sources.find(s => s.id === coverage.selectedId);
  const graphResult = coverage.preview?.graphPreview?.sources.find(s => s.id === coverage.selectedId);
  return <Stack gap="sm">
    <Fieldset legend="Visibility culling">
      <Stack gap="xs">
        <Checkbox label="Enable visibility culling" checked={visibilityAvailable && occlusionEnabled}
          disabled={!visibilityAvailable || busy || occlusionVisibilityLoading || !hostAvailable || project.target.game !== 'UYA'}
          onChange={event => setOcclusionEnabled(event.currentTarget.checked)} />
        <Checkbox label="Freeze current octant" checked={visibilityAvailable && occlusionFrozen}
          disabled={!visibilityAvailable || !occlusionEnabled || occlusionVisibilityLoading || !state.octant && !occlusionFrozen}
          onChange={event => setOcclusionFrozen(event.currentTarget.checked)} />
        {!visibilityAvailable && !occlusionVisibilityWarning && <Text size="xs" role="status">Build occlusion to preview which scene objects are hidden. Octant overlays are available without building visibility.</Text>}
        {occlusionVisibilityLoading && <Text size="xs" role="status">Loading occlusion masks…</Text>}
        {occlusionVisibilityError && <>
          <Text size="xs" c="red" role="alert">{occlusionVisibilityError}</Text>
          <Button variant="default" w="fit-content" disabled={busy || occlusionVisibilityLoading}
            onClick={reloadOcclusionVisibility}>Retry loading masks</Button>
        </>}
        {occlusionVisibilityWarning && <Text size="xs" c="yellow" role="status">{occlusionVisibilityWarning}</Text>}
        {visibilityAvailable && occlusionEnabled && occlusionVisibility && <Text size="xs" role="status">
          {occlusionFrozen ? 'Frozen' : 'Current'}: {state.octant ? `#${state.octant.id} (${state.octant.x}, ${state.octant.y}, ${state.octant.z})` : 'No octant — everything visible'}
          {' · '}{occlusionVisibility.source === 'generated' ? 'Baked' : 'Vanilla'} masks
          {' · '}{state.hiddenEntities.toLocaleString()} objects culled
        </Text>}
      </Stack>
    </Fieldset>
    <Fieldset legend="Overlays">
      <Stack gap="xs">
        <Checkbox label="Show coverage volumes" checked={coverage.showVolumes} onChange={event => coverage.setShowVolumes(event.currentTarget.checked)} />
        <Checkbox checked={Boolean(coverage.preview) && coverage.showPreview} disabled={!coverage.preview}
          label={`Show generated octants${coverage.preview ? ` (${coverage.preview.cells.length.toLocaleString()})` : ''}`}
          onChange={event => coverage.setShowPreview(event.currentTarget.checked)} />
        {!coverage.preview && !coverage.previewing && <Text size="xs" role="status">No generated octants yet. Use Generate octants in Build; if generation fails, see Log for details.</Text>}
        {coverage.previewStale && <Text size="xs" role="status">Generated octants are out of date. Generate again in Build.</Text>}
        {coverage.preview?.graphPreview && <>
          <Select label="Generated cell display" value={coverage.graphInspection ?? 'effective'}
            data={[{ value: 'effective', label: 'Final effective coverage' },
              { value: 'accepted', label: source?.graph ? `${source.name}: accepted cells` : 'Selected graph: accepted cells', disabled: !graphResult },
              { value: 'rejected', label: source?.graph ? `${source.name}: rejected cells` : 'Selected graph: rejected cells', disabled: !graphResult }]}
            onChange={value => { coverage.setGraphInspection(value === 'accepted' || value === 'rejected' ? value : undefined); coverage.setShowPreview(true); }} />
          <Text size="xs">{graphResult ? `${graphResult.accepted.length.toLocaleString()} accepted · ${graphResult.rejected.length.toLocaleString()} rejected cells before union and exclusions.`
            : 'Select a previewed graph in Volumes to inspect accepted or rejected cells.'}</Text>
        </>}
        <Checkbox checked={displayAvailable && showOcclusionOctants} disabled={!displayAvailable}
          label={`Show ${occlusionSamples ? 'qualified' : builtCoverage ? 'built' : 'Primary'} octants (${octantCount.toLocaleString()})`}
          onChange={event => setShowOcclusionOctants(event.currentTarget.checked)} />
        <Checkbox checked={displayAvailable && showOcclusionOctantIds} disabled={!displayAvailable}
          label="Show octant IDs" onChange={event => setShowOcclusionOctantIds(event.currentTarget.checked)} />
        <Checkbox checked={Boolean(occlusionSamples) && showOcclusionSamples} disabled={!occlusionSamples}
          label="Show qualified samples" onChange={event => setShowOcclusionSamples(event.currentTarget.checked)} />
        {occlusionSamplesStale && <Text size="xs" role="status">Qualified samples are out of date. Qualify again in Build.</Text>}
      </Stack>
    </Fieldset>
    <Accordion variant="contained">
      <Accordion.Item value="help">
        <Accordion.Control>Display guide</Accordion.Control>
        <Accordion.Panel>
          <Stack gap="xs">
            <Text size="xs">Visibility uses the latest baked masks, or vanilla masks before the first bake. Outside all octants, everything is visible. Unmapped objects stay visible.</Text>
            <Text size="xs">Freeze holds the current octant’s visibility while flying elsewhere. IDs label nearby octants independently of cube visibility; the frozen octant stays labeled at any distance.</Text>
            <Text size="xs">Generated octants preview the coverage recipe. Include shapes can restore graph cells rejected by Sticky. Build creates the in-game visibility masks.</Text>
            <Text size="xs">Sample squares mark centers, dots unchanged corners, diamonds and lines moved corners, and rings extra vertical-edge midpoints. Samples are visible through geometry.</Text>
            {occlusionSamples && <Text size="xs">Pink outlines mark {occlusionSamples.cells.filter(cell => cell.inwardCorners).length.toLocaleString()} octants using inward repairs. Pink crosses and paths show the affected samples.</Text>}
          </Stack>
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  </Stack>;
}
