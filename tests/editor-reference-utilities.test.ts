import assert from 'node:assert/strict';
import test from 'node:test';

import type { EditorEntity, EditorReference } from '../src/types/EditorRuntime.js';
import { clearEntityDrag, readEntityDrag, writeEntityDrag } from '../src/utils/EntityDrag.ts';
import {
  buildReferenceIndex, compatibleReferenceDropTarget, compatibleReferenceTargets,
  referenceNavigationTarget, referencePage,
} from '../src/utils/EditorReferences.ts';
import { compatibleMobyProperties, filterMobyPVarFields } from '../src/utils/MobyFields.ts';
import {
  flattenPVarFields, pvarByteIsModified, pvarFieldsAtOffset, pvarHexRowSegments, pvarHexWindow,
  pvarModifiedByteCount, structuredPVarFields,
} from '../src/utils/PVarHex.ts';

function entity(index: number): EditorEntity {
  return {
    id: String(index),
    name: `Moby ${index}`,
    layer: index % 2 ? 'mobys' : 'gameplay',
    transform: {
      position: { x: 0, y: 0, z: 0 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
    asset: { id: 'asset', kind: 'moby' },
    transformModes: ['translate', 'rotate', 'scale'],
    state: {
      dirty: index === 42,
      hidden: false,
      disabled: false,
      locked: index === 42,
      readOnly: false,
      invalid: false,
      missingAsset: index === 42,
    },
  };
}

test('reference indexes and pages keep large graph work bounded', () => {
  const references: EditorReference[] = Array.from({ length: 25_000 }, (_, index) => ({
    ownerEntityId: `owner-${index % 5}`,
    domain: 'entity',
    fieldKey: 'geometry.area.splines',
    nullable: true,
    targetKind: 'spline',
    targetEntityId: `target-${index}`,
    sourceValue: index,
    missing: false,
  }));
  const index = buildReferenceIndex(references);
  assert.equal(index.outgoing.get('owner-0')?.length, 5_000);
  assert.equal(index.incoming.get('target-24999')?.[0].ownerEntityId, 'owner-4');
  const last = referencePage(index.outgoing.get('owner-0') ?? [], 49);
  assert.equal(last.values.length, 100);
  assert.equal(last.pageCount, 50);
});

test('reference target filtering uses typed kinds and collision source rules', () => {
  const entities = [
    { ...entity(1), layer: 'ties', asset: { id: 'tie', kind: 'Tie' } },
    { ...entity(2), layer: 'shrubs', asset: { id: 'shrub', kind: 'Shrub' } },
    { ...entity(3), asset: undefined, geometry: { kind: 'spline', points: [] } },
    { ...entity(4), asset: undefined, layer: 'mobys' },
  ] satisfies EditorEntity[];
  assert.deepEqual(compatibleReferenceTargets({
    ownerEntityId: 'area', domain: 'entity', fieldKey: 'geometry.area.splines', nullable: true,
    targetKind: 'spline', sourceValue: 0, missing: false,
  }, entities).map((value) => value.id), ['3']);
  assert.deepEqual(compatibleReferenceTargets({
    ownerEntityId: 'collision', domain: 'entity', fieldKey: 'collision.attachment', nullable: false,
    targetKind: 'entity', missing: false,
  }, entities).map((value) => value.id), ['1', '2']);
  assert.deepEqual(compatibleReferenceTargets({
    ownerEntityId: 'moby', domain: 'entity', fieldKey: 'pvar.spawnMoby', nullable: true,
    targetKind: 'moby', sourceValue: -1, missing: false,
  }, entities).map((value) => value.id), ['4']);
});

test('reference drops accept only compatible entity ids', () => {
  const candidates = [entity(1), entity(2)];
  assert.equal(compatibleReferenceDropTarget('1', candidates), '1');
  assert.equal(compatibleReferenceDropTarget('3', candidates), undefined);
  assert.equal(compatibleReferenceDropTarget(undefined, candidates), undefined);
});

test('entity drag payload falls back to text for Electron native drops', () => {
  const values = new Map<string, string>();
  const transfer = {
    effectAllowed: 'none',
    setData: (format: string, value: string) => { values.set(format, value); },
    getData: (format: string) => values.get(format) ?? '',
  } as unknown as DataTransfer;
  writeEntityDrag(transfer, 'entity-1');
  assert.equal(readEntityDrag(transfer), 'entity-1');
  values.delete('application/x-horizon-forge-entity');
  assert.equal(readEntityDrag(transfer), 'entity-1');
  values.delete('text/plain');
  assert.equal(readEntityDrag(transfer), 'entity-1');
  clearEntityDrag();
  assert.equal(readEntityDrag(transfer), undefined);
});

test('moby multi-edit fields require matching OClass and descriptor metadata', () => {
  const first = {
    ...entity(1), sourceClassId: 0x400,
    mobyProperties: [{
      key: 'instance.bolts', label: 'Bolts', value: { kind: 'integer' as const, value: 1 }, editable: true,
      integerMinimum: 0, integerMaximum: 0x7fff_ffff,
    }],
  };
  const second = {
    ...entity(2), sourceClassId: 0x400,
    mobyProperties: [{
      key: 'instance.bolts', label: 'Bolts', value: { kind: 'integer' as const, value: 2 }, editable: true,
      integerMinimum: 0, integerMaximum: 0x7fff_ffff,
    }],
  };
  assert.deepEqual(compatibleMobyProperties([first, second]).map((value) => value.key), ['instance.bolts']);
  assert.deepEqual(compatibleMobyProperties([first, { ...second, sourceClassId: 0x401 }]), []);
  assert.deepEqual(compatibleMobyProperties([first, {
    ...second,
    mobyProperties: [{ ...second.mobyProperties[0], integerMaximum: 100 }],
  }]), []);
});

test('PVar field search retains matching nested hierarchy and supports offset queries', () => {
  const fields = [{
    path: 'settings', label: 'Settings', offset: 4, length: 8, kind: 'group' as const,
    editable: false, invalid: false, children: [
      { path: 'settings.enabled', label: 'Enabled', offset: 4, length: 1, kind: 'boolean' as const,
        value: { kind: 'boolean' as const, value: true }, editable: true, invalid: false, children: [] },
      { path: 'settings.count', label: 'Count', offset: 8, length: 4, kind: 'integer' as const,
        value: { kind: 'integer' as const, value: '2' }, editable: true, invalid: false, children: [] },
    ],
  }, {
    path: '#unknown-12', label: 'Unknown bytes', offset: 12, length: 4, kind: 'unknown' as const,
    value: { kind: 'bytes' as const, value: '00000000' }, editable: false, invalid: false, children: [],
  }];
  const nested = filterMobyPVarFields(fields, 'count');
  assert.deepEqual(nested.map((field) => field.path), ['settings']);
  assert.deepEqual(nested[0]!.children.map((field) => field.path), ['settings.count']);
  assert.deepEqual(filterMobyPVarFields(fields, '0xc').map((field) => field.path), ['#unknown-12']);
  assert.deepEqual(filterMobyPVarFields(fields, '').length, 2);
});

test('PVar raw view keeps large blobs windowed and maps nested overlapping fields', () => {
  const fields = [{
    path: 'settings', label: 'Settings', offset: 4, length: 8, kind: 'group' as const,
    editable: false, invalid: false, children: [
      { path: 'settings.value', label: 'Value', offset: 4, length: 4, kind: 'integer' as const,
        editable: true, invalid: false, children: [] },
      { path: 'settings.lowByte', label: 'Low byte', offset: 4, length: 1, kind: 'integer' as const,
        editable: true, invalid: false, children: [] },
    ],
  }];
  assert.deepEqual(flattenPVarFields(fields).map((field) => field.path),
    ['settings', 'settings.value', 'settings.lowByte']);
  assert.deepEqual(pvarFieldsAtOffset(fields, 4).map((field) => field.path),
    ['settings.lowByte', 'settings.value']);
  assert.deepEqual(pvarFieldsAtOffset(fields, 7).map((field) => field.path), ['settings.value']);

  const structured = structuredPVarFields([...fields, {
    path: '#unknown-12', label: 'Unknown bytes', offset: 12, length: 4, kind: 'unknown' as const,
    editable: false, invalid: false, children: [],
  }]);
  assert.deepEqual(flattenPVarFields(structured).map((field) => field.path),
    ['settings', 'settings.value', 'settings.lowByte']);

  const segments = pvarHexRowSegments(flattenPVarFields(fields).filter((field) => field.kind !== 'group'), 0, 16);
  assert.deepEqual(segments.map(({ field, start, width, containsStart }) => ({
    path: field.path, start, width, containsStart,
  })), [
    { path: 'settings.value', start: 10, width: 8, containsStart: true },
    { path: 'settings.lowByte', start: 16, width: 2, containsStart: true },
  ]);

  const first = pvarHexWindow(1_048_576, 0, 320);
  const middle = pvarHexWindow(1_048_576, 500_000, 320);
  assert.equal(first.rowCount, 65_536);
  assert.ok(first.endRow - first.startRow <= 32);
  assert.ok(middle.startRow > 0);
  assert.ok(middle.endRow - middle.startRow <= 32);
  assert.equal(middle.totalHeight, first.totalHeight);

  const modified = Uint8Array.of(0b1000_0001, 0b0000_0001);
  assert.equal(pvarByteIsModified(modified, 0), true);
  assert.equal(pvarByteIsModified(modified, 7), true);
  assert.equal(pvarByteIsModified(modified, 8), true);
  assert.equal(pvarByteIsModified(modified, 9), false);
  assert.equal(pvarModifiedByteCount(modified, 9), 3);
  assert.equal(pvarModifiedByteCount(Uint8Array.of(0xff), 3), 3);
  const maximumMask = new Uint8Array(1_048_576 / 8);
  maximumMask[maximumMask.length - 1] = 0x80;
  assert.equal(pvarModifiedByteCount(maximumMask, 1_048_576), 1);
});

test('reference navigation routes entities to the scene and assets to preview', () => {
  const entityReference: EditorReference = {
    ownerEntityId: 'owner', domain: 'entity', fieldKey: 'geometry.area.splines', nullable: true,
    targetKind: 'spline', targetEntityId: 'target', sourceValue: 7, missing: false,
  };
  const assetReference: EditorReference = {
    ownerEntityId: 'owner', domain: 'asset', fieldKey: 'entity.asset', nullable: true,
    targetKind: 'Moby', targetAssetId: 'asset', missing: false,
  };
  assert.deepEqual(referenceNavigationTarget(entityReference, 'outgoing'),
    { domain: 'entity', id: 'target', kind: 'spline' });
  assert.deepEqual(referenceNavigationTarget(entityReference, 'incoming'),
    { domain: 'entity', id: 'owner' });
  assert.deepEqual(referenceNavigationTarget(assetReference, 'outgoing'),
    { domain: 'asset', id: 'asset', kind: 'Moby' });
});

