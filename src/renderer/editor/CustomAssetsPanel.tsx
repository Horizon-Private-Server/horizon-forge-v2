import { Alert, Button, Checkbox, Select, Text } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';

import type { AssetExplorerItem } from '../../types/AssetExplorer.js';
import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import { buildAssetFamilies } from '../../utils/AssetExplorer.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { assetGridWindow } from '../../utils/VirtualGrid.ts';
import { AssetCard } from './AssetExplorerPanel.tsx';
import { useEditor } from './EditorContext.ts';

export function CustomAssetsPanel() {
  const { assetPreview, assetThumbnails, busy, execute, inspectAsset, project, sceneTreeColors } = useEditor();
  const [forceBackfaceCulling, setForceBackfaceCulling] = useState(false);
  const [importing, setImporting] = useState(false);
  const [error, setError] = useState('');
  const [scrollElement, setScrollElement] = useState<HTMLDivElement | null>(null);
  const [viewport, setViewport] = useState({ width: 0, height: 0, scrollTop: 0 });
  const families = useMemo(() => buildAssetFamilies(project.customModels.map(customModelItem), {
    game: project.baseLevel.game,
    region: project.baseLevel.region,
    revision: project.baseLevel.revision,
    level: project.baseLevel.level,
  }), [project.baseLevel, project.customModels]);

  useEffect(() => {
    if (!assetPreview?.variants.some((variant) => variant.tags.includes('custom-model'))) return;
    inspectAsset(families.find((family) => family.familyId === assetPreview.familyId), false);
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

  const importTie = () => {
    setImporting(true);
    setError('');
    void window.forge.chooseCustomTieImport(forceBackfaceCulling)
      .then(async (value) => {
        if (!value) return;
        const known = new Set(project.customModels.map((model) => model.assetId));
        if (!await execute({
          id: crypto.randomUUID(), kind: 'importCustomTie', entityIds: [], import: value,
        })) return;
        const snapshot = await window.forge.getEditorSnapshot();
        const imported = snapshot.customModels.find((model) => !known.has(model.assetId))
          ?? snapshot.customModels.find((model) => model.sourceName === value.sourceName);
        if (!imported) throw new Error('The imported custom TIE was not returned by the editor.');
        const family = buildAssetFamilies([customModelItem(imported)], {
          game: snapshot.target.game,
          region: snapshot.target.region,
          revision: snapshot.target.revision,
          level: snapshot.baseLevel.level,
        })[0];
        if (!family) throw new Error('The imported custom TIE could not be displayed.');
        inspectAsset(family);
      })
      .catch((cause: unknown) => setError(errorMessage(cause)))
      .finally(() => setImporting(false));
  };

  return <section aria-label="Custom Assets" className="editor-panel custom-assets-panel">
    <div className="custom-assets-controls">
      <Select
        allowDeselect={false}
        aria-label="Custom asset type"
        data={[{ value: 'ties', label: 'TIE' }]}
        defaultValue="ties"
        label="Asset type"
      />
      <Checkbox
        checked={forceBackfaceCulling}
        label="Force backface culling"
        onChange={(event) => setForceBackfaceCulling(event.currentTarget.checked)}
      />
      <Button
        disabled={busy || importing}
        fullWidth
        loading={importing}
        size="md"
        onClick={importTie}
      >Import custom TIE</Button>
      {error && <Alert color="red" title="Could not import asset"><Text size="sm">{error}</Text></Alert>}
    </div>
    <div className="asset-explorer-grid-scroll" ref={setScrollElement}>
      <div
        aria-label="Custom asset results"
        className="asset-explorer-grid"
        role="list"
        style={{ height: windowed.totalHeight }}
      >
        {families.slice(windowed.startIndex, windowed.endIndex).map((family, offset) => {
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
              color={sceneTreeColors.tie}
              family={family}
              root={scrollElement}
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
