import { Stack, Text } from '@mantine/core';
import { useMemo, useState } from 'react';

import { useEditorSnapshot } from './EditorContext.ts';
import {
  EditorDiagnosticList,
  EditorEmptyState,
  EditorFilter,
  EditorPanel,
  EditorProperty,
  EditorPropertyGrid,
  EditorTree,
} from './EditorPrimitives.tsx';
import { SceneViewport } from './SceneViewport.tsx';

export function ViewportPanel() {
  return <SceneViewport />;
}

export function SceneTreePanel() {
  const project = useEditorSnapshot();
  const [filter, setFilter] = useState('');
  const nodes = useMemo(() => {
    const query = filter.trim().toLocaleLowerCase();
    const layers = new Map<string, typeof project.entities>();
    for (const entity of project.entities) {
      if (query && !entity.name.toLocaleLowerCase().includes(query)) continue;
      const layer = layers.get(entity.layer);
      if (layer) layer.push(entity);
      else layers.set(entity.layer, [entity]);
    }
    return [...layers].sort(([left], [right]) => left.localeCompare(right)).map(([layer, entities]) => ({
      value: `layer:${layer}`,
      label: `${layer} (${entities.length})`,
      children: entities.map((entity) => ({ value: entity.id, label: entity.name })),
    }));
  }, [filter, project.entities]);

  return <EditorPanel label="Scene hierarchy">
    <Stack>
      <EditorFilter label="Filter scene hierarchy" value={filter} onChange={setFilter} />
      {nodes.length
        ? <EditorTree label="Scene hierarchy" nodes={nodes} selected={project.selection} multiple />
        : <EditorEmptyState message="No matching scene objects." />}
    </Stack>
  </EditorPanel>;
}

export function PropertiesPanel() {
  const project = useEditorSnapshot();
  const entity = project.selection.length === 1
    ? project.entities.find((candidate) => candidate.id === project.selection[0])
    : undefined;

  return <EditorPanel label="Properties">
    {entity ? <EditorPropertyGrid>
      <EditorProperty label="Name">{entity.name}</EditorProperty>
      <EditorProperty label="Layer">{entity.layer}</EditorProperty>
      <EditorProperty label="Entity ID">{entity.id}</EditorProperty>
      <EditorProperty label="Position">{formatVector(entity.transform.position)}</EditorProperty>
      <EditorProperty label="Rotation">{formatQuaternion(entity.transform.rotation)}</EditorProperty>
      <EditorProperty label="Scale">{formatVector(entity.transform.scale)}</EditorProperty>
    </EditorPropertyGrid> : <EditorEmptyState message={project.selection.length
      ? `${project.selection.length} objects selected.`
      : 'Select an object to inspect its properties.'} />}
  </EditorPanel>;
}

export function DiagnosticsPanel() {
  const project = useEditorSnapshot();
  const messages = project.diagnostics.map((diagnostic) => `${diagnostic.severity}: ${diagnostic.message}`);
  if (project.migrationPending) messages.unshift('warning: This project has a pending format migration.');
  return <EditorPanel label="Diagnostics">
    <EditorDiagnosticList messages={messages} />
  </EditorPanel>;
}

export function EditorWatermark() {
  return <Text c="dimmed" className="editor-empty">Use View → Reset Layout to restore editor panels.</Text>;
}

function formatVector(value: { x: number; y: number; z: number }): string {
  return `${value.x.toFixed(3)}, ${value.y.toFixed(3)}, ${value.z.toFixed(3)}`;
}

function formatQuaternion(value: { x: number; y: number; z: number; w: number }): string {
  return `${formatVector(value)}, ${value.w.toFixed(3)}`;
}
