import { Alert, Badge, Button, MultiSelect, SegmentedControl, Select, Text, TextInput } from '@mantine/core';
import { PlaceholderIcon } from '@phosphor-icons/react/dist/csr/Placeholder';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import type {
  AssetExplorerCategory,
  AssetExplorerFacets,
  AssetExplorerFamily,
  AssetExplorerItem,
  AssetExplorerPage,
  AssetExplorerQuery,
  AssetPreviewKind,
} from '../../types/AssetExplorer.js';
import type { SceneTreeColors, SceneTreeKind } from '../../types/SceneTree.js';
import { errorMessage } from '../../utils/Errors.ts';
import { ASSET_PLACEMENT_MIME } from '../../utils/AssetPlacement.ts';
import { AssetPreviewMeshMissingError, AssetThumbnailRuntime } from './AssetThumbnailRuntime.ts';
import {
  assetExplorerFilterCount,
  assetGridWindow,
  buildAssetFamilies,
  isStaleAssetExplorerCursor,
} from './EditorPanelState.ts';
import { useEditor } from './EditorContext.ts';

const CATEGORIES: { label: string; value: AssetExplorerCategory }[] = [
  { label: 'Ties', value: 'ties' },
  { label: 'Shrubs', value: 'shrubs' },
  { label: 'Mobys', value: 'mobys' },
  { label: 'Sky', value: 'skyShells' },
  { label: 'Textures', value: 'textures' },
];

const EMPTY_FACETS: AssetExplorerFacets = { games: [], levels: [], regions: [], revisions: [], tags: [] };

export function AssetExplorerPanel() {
  const { inspectAsset, project, sceneTreeColors } = useEditor();
  const [category, setCategory] = useState<AssetExplorerCategory>('ties');
  const [search, setSearch] = useState('');
  const [querySearch, setQuerySearch] = useState('');
  const [filters, setFilters] = useState<Pick<AssetExplorerQuery, 'game' | 'level' | 'region' | 'revision' | 'tags'>>({
    tags: [],
  });
  const [facets, setFacets] = useState<AssetExplorerFacets>(EMPTY_FACETS);
  const [items, setItems] = useState<AssetExplorerItem[]>([]);
  const [nextCursor, setNextCursor] = useState<string>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [scrollElement, setScrollElement] = useState<HTMLDivElement | null>(null);
  const [viewport, setViewport] = useState({ width: 0, height: 0, scrollTop: 0 });
  const [thumbnails, setThumbnails] = useState<AssetThumbnailRuntime | null>();
  const generation = useRef(0);
  const loadingRef = useRef(false);

  useEffect(() => {
    const timer = setTimeout(() => setQuerySearch(search.trim()), 180);
    return () => clearTimeout(timer);
  }, [search]);

  useEffect(() => {
    try {
      const runtime = new AssetThumbnailRuntime();
      setThumbnails(runtime);
      return () => runtime.dispose();
    } catch {
      setThumbnails(null);
    }
  }, []);

  const loadPage = useCallback(async (
    currentCategory: AssetExplorerCategory,
    currentSearch: string,
    currentFilters: typeof filters,
    cursor: string | undefined,
    append: boolean,
    version: number,
  ) => {
    if (loadingRef.current) return;
    loadingRef.current = true;
    setLoading(true);
    setError('');
    try {
      const query = (nextCursor?: string) => window.forge.queryAssetExplorer({
        category: currentCategory,
        search: currentSearch || undefined,
        ...currentFilters,
        cursor: nextCursor,
        limit: 64,
      });
      let restart = false;
      let page: AssetExplorerPage;
      try {
        page = await query(cursor);
      } catch (cause) {
        if (!cursor || !isStaleAssetExplorerCursor(cause)) throw cause;
        page = await query();
        restart = true;
      }
      if (generation.current !== version) return;
      setItems((current) => append && !restart ? [...current, ...page.items] : page.items);
      setFacets(page.facets);
      setNextCursor(page.nextCursor);
    } catch (cause) {
      if (generation.current === version) setError(errorMessage(cause));
    } finally {
      if (generation.current === version) {
        loadingRef.current = false;
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    const version = ++generation.current;
    loadingRef.current = false;
    setItems([]);
    setFacets(EMPTY_FACETS);
    setNextCursor(undefined);
    void loadPage(category, querySearch, filters, undefined, false, version);
    return () => {
      if (generation.current === version) generation.current += 1;
      void window.forge.cancelAssetExplorerQuery();
    };
  }, [category, filters, loadPage, querySearch]);

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

  const families = useMemo(() => buildAssetFamilies(items, {
    game: project.baseLevel.game,
    region: project.baseLevel.region,
    revision: project.baseLevel.revision,
    level: project.baseLevel.level,
  }), [items, project.baseLevel]);
  const windowed = assetGridWindow(families.length, viewport.width, viewport.height, viewport.scrollTop);
  useEffect(() => {
    if (error || !nextCursor || loading || windowed.endIndex < families.length - windowed.columns * 2) return;
    void loadPage(category, querySearch, filters, nextCursor, true, generation.current);
  }, [category, error, families.length, filters, loadPage, loading, nextCursor, querySearch,
    windowed.columns, windowed.endIndex]);

  const filterCount = assetExplorerFilterCount(filters);
  const clearFilters = () => {
    setSearch('');
    setQuerySearch('');
    setFilters({ tags: [] });
  };

  return <section aria-label="Asset Explorer" className="editor-panel asset-explorer-panel">
    <div className="asset-explorer-controls">
      <SegmentedControl
        aria-label="Asset category"
        data={CATEGORIES}
        fullWidth
        size="xs"
        value={category}
        onChange={(value) => setCategory(value as AssetExplorerCategory)}
      />
      <TextInput
        aria-label="Search assets"
        placeholder="Search assets…"
        value={search}
        onChange={(event) => setSearch(event.currentTarget.value)}
      />
      <details className="asset-explorer-filters">
        <summary>{filterCount ? `Filters (${filterCount})` : 'Filters'}</summary>
        <div className="asset-explorer-filter-grid">
          <Select aria-label="Filter assets by game" clearable data={facets.games} label="Game"
            value={filters.game ?? null} onChange={(game) => setFilters((value) => ({ ...value, game: game || undefined }))} />
          <Select aria-label="Filter assets by level" clearable data={facets.levels} label="Level"
            value={filters.level ?? null} onChange={(level) => setFilters((value) => ({ ...value, level: level || undefined }))} />
          <Select aria-label="Filter assets by region" clearable data={facets.regions} label="Region"
            value={filters.region ?? null} onChange={(region) => setFilters((value) => ({ ...value, region: region || undefined }))} />
          <Select aria-label="Filter assets by revision" clearable data={facets.revisions} label="Revision"
            value={filters.revision ?? null}
            onChange={(revision) => setFilters((value) => ({ ...value, revision: revision || undefined }))} />
          <MultiSelect aria-label="Filter assets by tags" clearable data={facets.tags} label="Tags" searchable
            value={filters.tags ?? []} onChange={(tags) => setFilters((value) => ({ ...value, tags }))} />
          <Button disabled={!filterCount && !search} size="xs" variant="subtle" onClick={clearFilters}>Clear all</Button>
        </div>
      </details>
    </div>
    {error && <Alert color="red" title="Could not load assets">{error}</Alert>}
    <div className="asset-explorer-grid-scroll" ref={setScrollElement}>
      <div
        aria-label={`${CATEGORIES.find((value) => value.value === category)?.label ?? 'Asset'} results`}
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
            role="listitem"
            className="asset-explorer-card-position"
            style={{
              width: windowed.cardWidth,
              transform: `translate(${column * (windowed.cardWidth + 8)}px, ${row * 218}px)`,
            }}
          >
            <AssetCard
              color={assetCategoryColor(family.category, sceneTreeColors)}
              family={family}
              root={scrollElement}
              runtime={thumbnails}
              onActivate={() => inspectAsset(family)}
            />
          </div>;
        })}
      </div>
      {!items.length && !loading && !error && <Text c="dimmed" className="editor-empty">No assets found.</Text>}
      {loading && <Text c="dimmed" className="asset-explorer-loading" role="status">Loading assets…</Text>}
    </div>
  </section>;
}

function AssetCard({ color, family, root, runtime, onActivate }: {
  color: string;
  family: AssetExplorerFamily;
  root: HTMLElement | null;
  runtime: AssetThumbnailRuntime | null | undefined;
  onActivate(): void;
}) {
  const item = family.variants.find((variant) => variant.assetId === family.representativeAssetId)
    ?? family.variants[0];
  const element = useRef<HTMLButtonElement>(null);
  const [thumbnail, setThumbnail] = useState<string>();
  const [failure, setFailure] = useState<'none' | 'meshless' | 'unavailable'>('none');
  const kind = previewKind(item);
  useEffect(() => {
    setThumbnail(undefined);
    setFailure('none');
    if (!root || !runtime || !kind || item.previewState === 'missingBlob') return;
    const cancellation = new AbortController();
    const observer = new IntersectionObserver((entries) => {
      if (!entries.some((entry) => entry.isIntersecting)) return;
      observer.disconnect();
      void runtime.get(item.assetId, kind, cancellation.signal)
        .then(setThumbnail)
        .catch((cause: unknown) => {
          if (cause instanceof DOMException && cause.name === 'AbortError') return;
          setFailure(cause instanceof AssetPreviewMeshMissingError && kind === 'moby' ? 'meshless' : 'unavailable');
        });
    }, { root });
    if (element.current) observer.observe(element.current);
    return () => {
      observer.disconnect();
      cancellation.abort();
    };
  }, [item.assetId, item.previewState, kind, root, runtime]);

  const previewLabel = item.previewState === 'missingBlob' ? 'Blob missing'
    : failure === 'unavailable' || runtime === null || !kind ? 'Preview unavailable'
      : thumbnail ? '' : 'Preview waiting';
  const draggable = item.canPlace && family.classId !== undefined && kind !== undefined;
  const texture = kind === 'texture';
  return <button
    ref={element}
    aria-label={`Inspect ${family.displayLabel}, ${categoryLabel(family.category)}, ${family.variants.length} exact variants${failure === 'meshless' ? ', no mesh data' : ''}`}
    className={`asset-explorer-card${texture ? ' asset-explorer-card-texture' : ''}`}
    draggable={draggable}
    type="button"
    onDragStart={(event) => {
      if (!draggable) {
        event.preventDefault();
        return;
      }
      event.dataTransfer.effectAllowed = 'copy';
      event.dataTransfer.setData(ASSET_PLACEMENT_MIME, JSON.stringify({
        assetId: item.assetId, kind, classId: family.classId,
      }));
    }}
    onClick={onActivate}
  >
    <span className={`asset-explorer-thumbnail${texture ? ' asset-explorer-thumbnail-texture' : ''}`}>
      {thumbnail ? <img alt="" src={thumbnail} />
        : failure === 'meshless'
          ? <PlaceholderIcon aria-hidden className="asset-explorer-thumbnail-placeholder" size={48} weight="duotone" />
          : <span>{previewLabel}</span>}
    </span>
    <span className="asset-explorer-card-meta">
      {family.classId === undefined
        ? <span className="asset-explorer-card-name">{family.displayLabel.replace(/^[^:]+:/, '')}</span>
        : <Badge className="asset-explorer-class-badge" variant="outline" style={{ borderColor: color, color }}>
          {`${categoryLabel(family.category).replace(/s$/, '')} 0x${family.classId
            .toString(16).toUpperCase().padStart(4, '0')}`}
        </Badge>}
    </span>
  </button>;
}

function assetCategoryColor(category: AssetExplorerCategory, colors: SceneTreeColors): string {
  const kind: SceneTreeKind = ({
    ties: 'tie', shrubs: 'shrub', mobys: 'moby', skyShells: 'sky', textures: 'object',
  })[category] as SceneTreeKind;
  return colors[kind];
}

function previewKind(item: AssetExplorerItem): AssetPreviewKind | undefined {
  if (item.category === 'ties') return 'tie';
  if (item.category === 'shrubs') return 'shrub';
  if (item.category === 'mobys') return 'moby';
  if (item.category === 'textures') return 'texture';
  return undefined;
}

function categoryLabel(category: AssetExplorerCategory): string {
  return CATEGORIES.find((value) => value.value === category)?.label ?? category;
}
