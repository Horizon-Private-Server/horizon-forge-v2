import { useCallback, useEffect, useRef, useState } from 'react';
import type { OcclusionCoverageAuthoring, OcclusionCoverageMarker, OcclusionCoverageSource } from '../../types/OcclusionCoverage.js';
import type { EditorSelectionAction, EditorSelectionActions } from '../../types/EditorSelectionActions.js';
import { createOcclusionCoverageMarker, duplicateOcclusionCoverageSource, isOcclusionCoverageState, occlusionSeedLabel, occlusionVolumeContains, OCCLUSION_COVERAGE_LIMITS } from '../../utils/OcclusionCoverage.ts';
import { useLogReporter } from '../log/UseLog.ts';

type Clipboard = { kind: 'seed'; marker: OcclusionCoverageMarker } | { kind: 'source'; source: OcclusionCoverageSource };

export function useOcclusionCoverageActions(projectKey: string, authoring: OcclusionCoverageAuthoring, busy: boolean,
  register: (actions?: EditorSelectionActions) => void) {
  const clipboard = useRef<Clipboard | undefined>(undefined);
  const [clipboardRevision, setClipboardRevision] = useState(0);
  const current = useRef({ authoring, busy }); current.current = { authoring, busy };
  const pending = useRef(false);
  const warn = useLogReporter('Occlusion', 'warning');
  useEffect(() => { clipboard.current = undefined; setClipboardRevision(value => value + 1); }, [projectKey]);

  const handle = useCallback((action: EditorSelectionAction) => {
    const { authoring: a, busy } = current.current;
    const state = a.snapshot?.state;
    if (!state || busy || a.loading || pending.current) return;
    const source = state.sources.find(s => s.id === a.selectedId);
    const marker = source?.graph?.markers.find(m => m.id === a.selectedMarkerId);
    if (action === 'copyEntities') {
      if (!source || a.selectedMarkerId && !marker) return;
      clipboard.current = marker ? { kind: 'seed', marker: structuredClone(marker) } : { kind: 'source', source: structuredClone(source) };
      setClipboardRevision(value => value + 1); return;
    }
    let updated = state, selected = a.selectedId, selectedMarker = a.selectedMarkerId;
    if (action === 'deleteEntities') {
      if (!source || a.selectedMarkerId && !marker) return;
      if (marker) {
        const value = { ...source, graph: { ...source.graph!, markers: source.graph!.markers.filter(m => m.id !== marker.id) } };
        updated = { ...state, sources: state.sources.map(s => s.id === source.id ? value : s) }; selectedMarker = undefined;
      } else { updated = { ...state, sources: state.sources.filter(s => s.id !== source.id) }; selected = undefined; }
    } else {
      const copied = action === 'pasteEntities' ? clipboard.current
        : marker ? { kind: 'seed' as const, marker } : source ? { kind: 'source' as const, source } : undefined;
      if (!copied) return;
      if (copied.kind === 'seed') {
        if (!source?.graph) { warn('Select a graph to paste a seed.'); return; }
        const copy = createOcclusionCoverageMarker(occlusionSeedLabel(source.graph.markers.length), copied.marker.position);
        const value = { ...source, graph: { ...source.graph, markers: [...source.graph.markers, copy] } };
        updated = { ...state, sources: state.sources.map(s => s.id === source.id ? value : s) }; selectedMarker = copy.id;
      } else {
        const copy = duplicateOcclusionCoverageSource(copied.source);
        updated = { ...state, sources: [...state.sources, copy] }; selected = copy.id; selectedMarker = undefined;
      }
    }
    if (!isOcclusionCoverageState(updated)) {
      warn('The edit exceeds a coverage volume/seed limit, or the seed does not fit inside the selected graph.'); return;
    }
    pending.current = true;
    void a.commit(updated).then(ok => { if (ok) a.select(selected, selectedMarker); }).finally(() => { pending.current = false; });
  }, [warn]);

  const state = authoring.snapshot?.state;
  const source = state?.sources.find(s => s.id === authoring.selectedId);
  const marker = source?.graph?.markers.find(m => m.id === authoring.selectedMarkerId);
  const editable = Boolean(source && (!authoring.selectedMarkerId || marker) && !busy && !authoring.loading);
  const count = state?.sources.reduce((n, s) => n + (s.graph?.markers.length ?? 0), 0) ?? 0;
  const copied = clipboard.current;
  const canPaste = Boolean(state && copied && !busy && !authoring.loading && (copied.kind === 'seed'
    ? source?.graph && source.graph.markers.length < OCCLUSION_COVERAGE_LIMITS.markersPerGraph
      && count < OCCLUSION_COVERAGE_LIMITS.markers
      && occlusionVolumeContains(source, copied.marker.position)
    : state.sources.length < OCCLUSION_COVERAGE_LIMITS.sources
      && count + (copied.source.graph?.markers.length ?? 0) <= OCCLUSION_COVERAGE_LIMITS.markers));
  useEffect(() => {
    register(authoring.selectedId ? { canCopy: editable, canMutate: editable, canPaste, handle } : undefined);
    return () => register(undefined);
  }, [register, authoring.selectedId, editable, canPaste, handle, clipboardRevision]);
}
