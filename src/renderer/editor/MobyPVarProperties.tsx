import { BracketsCurlyIcon } from '@phosphor-icons/react/dist/csr/BracketsCurly';
import { ListBulletsIcon } from '@phosphor-icons/react/dist/csr/ListBullets';
import { Button, Code, Fieldset, Group, Stack, Tabs, Text, TextInput } from '@mantine/core';
import { useMemo, useState } from 'react';

import type {
  EditorEntity, EditorMobyPVarFieldDescriptor, EditorMobyPVarValue,
} from '../../types/EditorRuntime.js';
import { structuredPVarFields } from '../../utils/PVarHex.ts';
import { useEditor } from './EditorContext.ts';
import { filterMobyPVarFields } from '../../utils/MobyFields.ts';
import { MobyPVarFields, pvarFieldElementId } from './MobyPVarFields.tsx';
import { MobyPVarHexView } from './MobyPVarHexView.tsx';

type PVarTab = 'fields' | 'raw';

export function MobyPVarProperties({ entity, disabled }: { entity: EditorEntity; disabled: boolean }) {
  const { execute, navigateToEntity, project } = useEditor();
  const [tab, setTab] = useState<PVarTab>('fields');
  const [search, setSearch] = useState('');
  const [focusedPath, setFocusedPath] = useState<string>();
  const pvar = entity.mobyPVar;
  const structuredFields = useMemo(() => structuredPVarFields(pvar?.fields ?? []), [pvar?.fields]);
  if (!pvar) return null;
  const fields = filterMobyPVarFields(structuredFields, search);
  const commit = (field: EditorMobyPVarFieldDescriptor, value: EditorMobyPVarValue) => execute({
    id: crypto.randomUUID(),
    kind: 'setMobyPVarField',
    entityIds: [entity.id],
    pvar: {
      fieldPath: field.path,
      expectedClassId: entity.sourceClassId!,
      expectedDatasetId: pvar.datasetId,
      expectedDatasetVersion: pvar.datasetVersion,
      expectedSchemaVersion: pvar.schemaVersion,
      expectedSchemaFingerprint: pvar.schemaFingerprint,
      expectedStateFingerprint: pvar.stateFingerprint,
      value,
    },
  });
  const showRaw = (path: string) => {
    setFocusedPath(path);
    setTab('raw');
  };
  const showField = (path: string) => {
    setFocusedPath(path);
    setSearch('');
    setTab('fields');
    requestAnimationFrame(() => {
      const element = document.getElementById(pvarFieldElementId(path));
      element?.scrollIntoView({ block: 'nearest' });
      element?.focus({ preventScroll: true });
    });
  };

  return <Fieldset legend="PVar">
    <Stack gap="xs">
      <Group justify="space-between" wrap="wrap">
        <Text c="dimmed" size="xs">{pvar.schemaSource} · {pvar.length} bytes</Text>
        {pvar.hasData && <Code>{pvar.stateFingerprint.slice(0, 12)}</Code>}
      </Group>
      {pvar.diagnostic && <Text c={pvar.hasData ? 'yellow' : 'dimmed'} size="xs">{pvar.diagnostic}</Text>}
      {!pvar.hasData && pvar.canInitialize && <Button size="xs" disabled={disabled} onClick={() => void execute({
        id: crypto.randomUUID(), kind: 'initializeMobyPVar', entityIds: [entity.id],
      })}>Initialize PVar</Button>}
      {pvar.hasData && <Tabs value={tab} onChange={(value) => value && setTab(value as PVarTab)} keepMounted={false}>
        <Tabs.List grow>
          <Tabs.Tab value="fields" leftSection={<ListBulletsIcon size={14} />}>Fields</Tabs.Tab>
          <Tabs.Tab value="raw" leftSection={<BracketsCurlyIcon size={14} />}>Raw bytes</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="fields" pt="xs">
          <Stack gap="xs">
            <TextInput
              aria-label="Search PVar fields"
              placeholder="Search fields or offsets"
              size="xs"
              value={search}
              onChange={(event) => setSearch(event.currentTarget.value)}
            />
            {fields.length === 0
              ? <Text c="dimmed" size="xs">{structuredFields.length === 0
                ? 'No structured fields are defined for this PVar.'
                : 'No matching fields.'}</Text>
              : <MobyPVarFields fields={fields} entity={entity} entities={project.entities}
                disabled={disabled} searching={search.trim().length > 0} onCommit={commit}
                onNavigate={navigateToEntity} onShowRaw={showRaw} />}
            <Text c="dimmed" size="xs">
              Field writes are resolved against the active schema; unmapped bytes are available in Raw bytes.
            </Text>
          </Stack>
        </Tabs.Panel>
        <Tabs.Panel value="raw" pt="xs">
          {pvar.rawData
            ? <MobyPVarHexView data={pvar.rawData} modifiedByteMask={pvar.modifiedByteMask}
              fields={pvar.fields} entities={project.entities} focusedPath={focusedPath}
              onShowField={showField} />
            : <Text c="dimmed" size="xs">Raw PVar bytes are unavailable for this snapshot.</Text>}
        </Tabs.Panel>
      </Tabs>}
    </Stack>
  </Fieldset>;
}
