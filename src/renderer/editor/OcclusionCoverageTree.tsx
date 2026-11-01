import { ActionIcon } from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye';
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash';
import { PowerIcon } from '@phosphor-icons/react/dist/csr/Power';
import { CrosshairIcon } from '@phosphor-icons/react/dist/csr/Crosshair';
import { PRIMARY_OCCLUSION_SOURCE_ID } from '../../utils/OcclusionCoverage.ts';
import { useEditor } from './EditorContext.ts';
import { EditorTree } from './EditorPrimitives.tsx';
import { SceneTreeLabel } from './SceneTreeNodes.tsx';

export function OcclusionCoverageTree() {
  const { coverage, terrain, busy, setShowOcclusionOctants } = useEditor();
  const state = coverage.snapshot!.state;
  const disabled = busy || coverage.loading;
  const counts = new Map(coverage.preview?.sources.map(source => [source.id, source.cells]));
  const actions = (name: string, visible: boolean, enabled: boolean, onShow: () => void,
    onEnable: () => void, onFocus: () => void, visibilityDisabled = false) => <span className="scene-tree-actions">
    <ActionIcon size="xs" variant="subtle" color={visible ? 'blue' : 'gray'} disabled={visibilityDisabled}
      aria-label={`${visible ? 'Hide' : 'Show'} ${name}`} title={`${visible ? 'Hide' : 'Show'} ${name}`}
      onClick={event => { event.stopPropagation(); onShow(); }}>
      {visible ? <EyeIcon size={13} /> : <EyeSlashIcon size={13} />}
    </ActionIcon>
    <ActionIcon size="xs" variant="subtle" color={enabled ? 'teal' : 'gray'} disabled={disabled}
      aria-label={`${enabled ? 'Disable' : 'Enable'} ${name}`} title={`${enabled ? 'Disable' : 'Enable'} ${name}`}
      onClick={event => { event.stopPropagation(); onEnable(); }}>
      <PowerIcon size={13} weight={enabled ? 'fill' : 'regular'} />
    </ActionIcon>
    <ActionIcon size="xs" variant="subtle" aria-label={`Focus ${name}`} title={`Focus ${name}`}
      onClick={event => { event.stopPropagation(); onFocus(); }}>
      <CrosshairIcon size={13} />
    </ActionIcon>
  </span>;
  const focusPrimary = () => {
    if (state.primaryEnabled) setShowOcclusionOctants(true);
    coverage.focus(PRIMARY_OCCLUSION_SOURCE_ID);
  };
  const nodes: TreeNodeData[] = [{
    value: PRIMARY_OCCLUSION_SOURCE_ID,
    label: <span className="scene-tree-node">
      <SceneTreeLabel kind="occlusionOctant" dot>Primary · {state.primaryEnabled ? `${(terrain?.occlusionOctants.length ?? 0).toLocaleString()} octants` : 'disabled'}</SceneTreeLabel>
      {actions('Primary', state.primaryEnabled && !coverage.hiddenSources.has(PRIMARY_OCCLUSION_SOURCE_ID), state.primaryEnabled,
        () => { const visible = coverage.hiddenSources.has(PRIMARY_OCCLUSION_SOURCE_ID); coverage.setSourceVisible(PRIMARY_OCCLUSION_SOURCE_ID, visible); if (visible) setShowOcclusionOctants(true); },
        () => void coverage.commit({ ...state, primaryEnabled: !state.primaryEnabled }), focusPrimary, !state.primaryEnabled)}
    </span>,
  }, ...state.sources.map(source => ({
    value: source.id,
    label: <span className="scene-tree-node" title={source.name}>
      <SceneTreeLabel kind={source.cylinder ? 'cylinder' : 'cuboid'} dot>
        {source.name} · {source.cylinder ? 'Cylinder' : 'Box'}
        {source.graph && ` · Seed graph${source.graph.disabled ? ' off' : ''} (${source.graph.markers.length} seeds)`}
        {' · '}{source.role === 'include' ? 'Include' : 'Exclude'}{!source.enabled && ' · disabled'}
        {coverage.preview && !coverage.previewStale && ` · ${counts.get(source.id)?.toLocaleString() ?? '0'} cells`}
      </SceneTreeLabel>
      {actions(source.name, coverage.showVolumes && !coverage.hiddenSources.has(source.id), source.enabled,
        () => { const visible = !coverage.showVolumes || coverage.hiddenSources.has(source.id); coverage.setSourceVisible(source.id, visible); if (visible) coverage.setShowVolumes(true); },
        () => void coverage.commit({ ...state, sources: state.sources.map(s => s.id === source.id ? { ...s, enabled: !s.enabled } : s) }),
        () => coverage.focus(source.id))}
    </span>,
  }))];
  return <div className="occlusion-coverage-tree"><EditorTree label="Occlusion coverage" nodes={nodes} selected={coverage.selectedId ? [coverage.selectedId] : []}
    onSelectionChange={values => coverage.select(values[0])}
    onActivate={id => id === PRIMARY_OCCLUSION_SOURCE_ID ? focusPrimary() : coverage.focus(id)} /></div>;
}
