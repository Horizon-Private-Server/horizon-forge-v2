import { Button, Checkbox, Select, Text } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';

import type { AssetExplorerItem } from '../../types/AssetExplorer.js';
import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import { buildAssetFamilies } from '../../utils/AssetExplorer.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { assetGridItemVisible, assetGridWindow } from '../../utils/VirtualGrid.ts';
import { AssetCard } from './AssetExplorerPanel.tsx';
import { useEditor } from './EditorContext.ts';

import { forgeLog } from '../log/LogStore.ts';
import { useLogReporter } from '../log/UseLog.ts';

export function CustomAssetsPanel() {
  const { assetPreview, assetThumbnails, busy, execute, inspectAsset, project, sceneTreeColors } = useEditor();
  const [forceBackfaceCulling, setForceBackfaceCulling] = useState(false);
  const [assetType, setAssetType] = useState<'ties' | 'tfrags'>('ties');
  const [importing, setImporting] = useState(false);
  const reportError = useLogReporter('Custom assets');
  const [scrollElement, setScrollElement] = useState<HTMLDivElement | null>(null);
  const [viewport, setViewport] = useState({ width: 0, height: 0, scrollTop: 0 });
  const families = useMemo(() => buildAssetFamilies(assetType === 'ties'
    ? project.customModels.map(customModelItem)
    : customTfragItems(project), {
    game: project.baseLevel.game,
    region: project.baseLevel.region,
    revision: project.baseLevel.revision,
    level: project.baseLevel.level,
  }), [assetType, project]);

  useEffect(() => {
    const preview = assetPreview;
    const previewVariant = preview?.variants.find((variant) => variant.tags.includes('custom-model')
      || variant.tags.includes('custom-tfrag'));
    if (!preview || !previewVariant) return;
    const family = families.find((candidate) => candidate.familyId === preview.familyId
      || candidate.variants.some((variant) => variant.previewRevision
        && variant.previewRevision === previewVariant.previewRevision));
    if (family && family !== preview) inspectAsset(family, false);
  }, [assetPreview?.familyId, families, inspectAsset]);

  useEffect(() => {
    if (!scrollElement) return;
    let frame = 0;
    const update = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        setViewport({
          width: scrollElement.clientWidth,
          height: scrollElement.clientHeight,
          scrollTop: scrollElement.scrollTop,
        });
      });
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(scrollElement);
    scrollElement.addEventListener('scroll', update, { passive: true });
    return () => {
      observer.disconnect();
      scrollElement.removeEventListener('scroll', update);
      if (frame) cancelAnimationFrame(frame);
    };
  }, [scrollElement]);

  const windowed = assetGridWindow(families.length, viewport.width, viewport.height, viewport.scrollTop);

  const importAsset = () => {
    setImporting(true);
    const choose = assetType === 'ties'
      ? window.forge.chooseCustomTieImport(forceBackfaceCulling)
      : window.forge.chooseCustomTfragImport();
    void choose
      .then(async (value) => {
        if (!value) return;
        const known = new Set(assetType === 'ties'
          ? project.customModels.map((model) => model.assetId)
          : customTfragItems(project).map((item) => item.previewRevision!));
        if (!await execute({
          id: crypto.randomUUID(),
          kind: assetType === 'ties' ? 'importCustomTie' : 'importCustomTfrag',
          entityIds: [],
          import: value,
        })) return;
        const snapshot = await window.forge.getEditorSnapshot();
        const importedItem = assetType === 'ties'
          ? snapshot.customModels.map(customModelItem).find((item) => !known.has(item.assetId))
            ?? snapshot.customModels.map(customModelItem)
              .find((item) => item.displayLabel === value.sourceName)
          : customTfragItems(snapshot).find((item) => !known.has(item.previewRevision!))
            ?? customTfragItems(snapshot).find((item) => item.displayLabel === value.sourceName);
        if (!importedItem)
          throw new Error(`The imported custom ${assetType === 'ties' ? 'TIE' : 'tfrag'} was not returned by the editor.`);
        const family = buildAssetFamilies([importedItem], {
          game: snapshot.target.game,
          region: snapshot.target.region,
          revision: snapshot.target.revision,
          level: snapshot.baseLevel.level,
        })[0];
        if (!family) throw new Error('The imported custom asset could not be displayed.');
        forgeLog.write('Custom assets', 'success', `Imported ${value.sourceName}.`);
        inspectAsset(family);
      })
      .catch((cause: unknown) => reportError(errorMessage(cause)))
      .finally(() => setImporting(false));
  };

  return <section aria-label="Custom Assets" className="editor-panel custom-assets-panel">
    <div className="custom-assets-controls">
      <Select
        allowDeselect={false}
        aria-label="Custom asset type"
        data={[{ value: 'ties', label: 'TIE' }, { value: 'tfrags', label: 'Tfrag terrain' }]}
        value={assetType}
        label="Asset type"
        onChange={(value) => setAssetType(value === 'tfrags' ? 'tfrags' : 'ties')}
      />
      <Checkbox
        checked={forceBackfaceCulling}
        disabled={assetType === 'tfrags'}
        label="Force backface culling"
        onChange={(event) => setForceBackfaceCulling(event.currentTarget.checked)}
      />
      <Button
        disabled={busy || importing}
        fullWidth
        loading={importing}
        size="md"
        title={assetType === 'tfrags'
          ? 'Selecting an existing source rebuilds and replaces its imported terrain.'
          : undefined}
        onClick={importAsset}
      >{assetType === 'ties' ? 'Import custom TIE' : 'Import or re-import tfrag'}</Button>
    </div>
    <div className="asset-explorer-grid-scroll" ref={setScrollElement}>
      <div
        aria-label="Custom asset results"
        className="asset-explorer-grid"
        role="list"
        style={{ height: windowed.totalHeight }}
      >
        {viewport.height > 0 && families.slice(windowed.startIndex, windowed.endIndex).map((family, offset) => {
          const index = windowed.startIndex + offset;
          const row = Math.floor(index / windowed.columns);
          const column = index % windowed.columns;
          return <div
            key={family.familyId}
            className="asset-explorer-card-position"
            role="listitem"
            style={{
              width: windowed.cardWidth,
              transform: `translate(${column * (windowed.cardWidth + 8)}px, ${row * 218}px)`,
            }}
          >
            <AssetCard
              color={assetType === 'ties' ? sceneTreeColors.tie : sceneTreeColors.tfrag}
              family={family}
              visible={assetGridItemVisible(index, windowed.columns, viewport.height, viewport.scrollTop)}
              runtime={assetThumbnails}
              onActivate={() => inspectAsset(family)}
            />
          </div>;
        })}
      </div>
      {!families.length && <Text c="dimmed" className="editor-empty">No custom assets imported.</Text>}
    </div>
  </section>;
}

function customTfragItems(snapshot: EditorSnapshot): AssetExplorerItem[] {
  const imports = new Map<string, EditorSnapshot['entities'][number]>();
  snapshot.entities.forEach((entity) => {
    const importId = entity.tfrag?.customImportId;
    if (importId && entity.asset && !imports.has(importId)) imports.set(importId, entity);
  });
  return [...imports].map(([importId, entity]) => ({
    assetId: entity.asset!.id,
    category: 'tfrags',
    displayLabel: entity.tfrag!.customSourceName ?? entity.name,
    canonicalFormatVersion: 1,
    byteSize: entity.tfrag?.diagnostics?.sourceDataLength ?? 0,
    aliases: [`tfrag:${importId.slice(0, 12)}`],
    tags: ['tfrag', 'custom-tfrag', 'project-attached'],
    sources: [],
    classIds: [],
    previewState: 'notCached',
    canPlace: true,
    previewRevision: importId,
  }));
}

function customModelItem(model: EditorSnapshot['customModels'][number]): AssetExplorerItem {
  return {
    assetId: model.assetId,
    category: 'ties',
    displayLabel: model.sourceName,
    canonicalFormatVersion: 2,
    byteSize: 0,
    aliases: [`tie:${model.classId}`, `tie:0x${model.classId.toString(16).padStart(4, '0')}`],
    tags: ['tie', 'custom-model', 'project-attached'],
    sources: [],
    classIds: [model.classId],
    previewState: 'notCached',
    canPlace: true,
    previewRevision: model.nativeAssetId,
  };
}
