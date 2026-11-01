import { Accordion, Button, Fieldset, Group, Menu, Stack, Text } from '@mantine/core';
import { CaretDownIcon } from '@phosphor-icons/react/dist/csr/CaretDown';
import { PlusIcon } from '@phosphor-icons/react/dist/csr/Plus';
import { createOcclusionCoverageSource, duplicateOcclusionCoverageSource, OCCLUSION_COVERAGE_LIMITS, PRIMARY_OCCLUSION_SOURCE_ID } from '../../utils/OcclusionCoverage.ts';
import { useEditor } from './EditorContext.ts';
import { OcclusionCoverageTree } from './OcclusionCoverageTree.tsx';
import { OcclusionCoverageGraphEditor } from './OcclusionCoverageGraphEditor.tsx';
import { OcclusionCoverageSourceEditor } from './OcclusionCoverageSourceEditor.tsx';

export function OcclusionCoverageControls() {
  const { project, busy, coverage: authoring } = useEditor();
  const { snapshot: coverage, loading, selectedId: selected, select: setSelected, available, commit } = authoring;
  const state = coverage?.state, disabled = busy || loading;
  const source = state?.sources.find(value => value.id === selected);
  const canAdd = Boolean(state && state.sources.length < OCCLUSION_COVERAGE_LIMITS.sources);
  const add = async (kind: 'box' | 'cylinder') => {
    if (!state) return;
    const position = authoring.placementPosition(16)
      ?? project.entities.find(entity => entity.id === project.selection[0])?.transform.position;
    const source = createOcclusionCoverageSource(kind, `${kind === 'box' ? 'Box' : 'Cylinder'} ${state.sources.length + 1}`, position);
    if (await commit({ ...state, sources: [...state.sources, source] })) setSelected(source.id);
  };
  return <Stack gap="sm">
    {!available && <Text size="xs" c="dimmed">Coverage editing is unavailable.</Text>}
    {available && loading && !state && <Text size="xs" role="status">Loading coverage…</Text>}
    {available && state && <>
      <Group gap="xs" justify="space-between">
        <Menu position="bottom-start" withinPortal>
          <Menu.Target>
            <Button variant="default" disabled={disabled || !canAdd}
              leftSection={<PlusIcon size={14} />} rightSection={<CaretDownIcon size={12} />}>Add volume</Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item onClick={() => void add('box')}>Box</Menu.Item>
            <Menu.Item onClick={() => void add('cylinder')}>Cylinder</Menu.Item>
          </Menu.Dropdown>
        </Menu>
        <Button variant="subtle" disabled={disabled}
          title="Toggle the original octants; the originals are retained."
          onClick={() => void commit({ ...state, primaryEnabled: !state.primaryEnabled })}>
          {state.primaryEnabled ? 'Start from scratch' : 'Restore Primary'}
        </Button>
      </Group>
      <OcclusionCoverageTree />
      {!selected && <Text size="xs" c="dimmed">Select a volume to edit. Row controls show, enable or focus it.</Text>}
      {selected === PRIMARY_OCCLUSION_SOURCE_ID && <Text size="xs">
        Primary contains the original or imported octants. {state.primaryEnabled ? 'It contributes to coverage.' : 'It is disabled; its octants and masks are inactive.'}
        {' '}Original octants are retained when disabled.
      </Text>}
      {source && <Fieldset legend="Volume properties">
        <Stack gap="sm">
          <OcclusionCoverageSourceEditor key={`${source.id}:${JSON.stringify(source)}`} source={source} state={state} disabled={disabled}
            cylinderGraphsAvailable={project.capabilities.includes('editor.occlusion-coverage.graph-shapes')}
            canDuplicate={canAdd && state.sources.reduce((n, s) => n + (s.graph?.markers.length ?? 0), source.graph?.markers.length ?? 0) <= OCCLUSION_COVERAGE_LIMITS.markers}
            onApply={updated => void commit({ ...state, sources: state.sources.map(s => s.id === updated.id ? updated : s) })}
            onDelete={() => { void commit({ ...state, sources: state.sources.filter(s => s.id !== source.id) }).then(ok => { if (ok) setSelected(undefined); }); }}
            onDuplicate={() => {
              const duplicate = duplicateOcclusionCoverageSource(source);
              void commit({ ...state, sources: [...state.sources, duplicate] }).then(ok => { if (ok) setSelected(duplicate.id); });
            }} />
        </Stack>
      </Fieldset>}
      {source?.graph && <OcclusionCoverageGraphEditor source={source} authoring={authoring} disabled={disabled} />}
      <Text size="xs" c="dimmed">Use the Build tab to generate octants, qualify samples and bake visibility.</Text>
      <Accordion variant="contained">
        <Accordion.Item value="help">
          <Accordion.Control>Authoring help</Accordion.Control>
          <Accordion.Panel>
            <Stack gap="xs">
              <Text size="xs">New volumes appear ahead of the viewport camera. Use Move, Rotate or Scale to shape coverage; Escape cancels a drag.</Text>
              <Text size="xs">Solid outlines include cells; dashed outlines exclude cells. Show or hide volumes in the Viewport tab to select objects underneath.</Text>
              <Text size="xs">Edits mark generated results out of date. Generation runs only when requested in the Build tab.</Text>
            </Stack>
          </Accordion.Panel>
        </Accordion.Item>
      </Accordion>
    </>}
  </Stack>;
}
