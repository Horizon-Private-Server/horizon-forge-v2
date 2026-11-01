import { useCallback, useEffect, useRef, useState } from 'react';
import type { EditorSnapshot, ProjectVector3 } from '../../types/EditorRuntime.js';
import type { OcclusionCoverageAuthoring, OcclusionCoveragePreview, OcclusionCoverageSnapshot, OcclusionCoverageState } from '../../types/OcclusionCoverage.js';
import { PRIMARY_OCCLUSION_SOURCE_ID } from '../../utils/OcclusionCoverage.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { useLogReporter } from '../log/UseLog.ts';

export function useOcclusionCoverage(project: EditorSnapshot, hostAvailable: boolean, busy: boolean,
  update: (fingerprint: string, state: OcclusionCoverageState) => Promise<OcclusionCoverageSnapshot>,
  clearEntitySelection: () => void): OcclusionCoverageAuthoring {
  const [snapshot, setSnapshot] = useState<OcclusionCoverageSnapshot>();
  const [loading, setLoading] = useState(true);
  const [updating, setUpdating] = useState(false);
  const [selectedId, setSelectedId] = useState<string>();
  const [selectedMarkerId, setSelectedMarkerId] = useState<string>();
  const [showVolumes, setShowVolumes] = useState(false), [hiddenSources, setHiddenSources] = useState<Set<string>>(new Set());
  const [focusRequest, setFocusRequest] = useState<{ id: string; markerId?: string; serial: number }>();
  const [preview, setPreview] = useState<OcclusionCoveragePreview>();
  const [showPreview, setShowPreview] = useState(false), [previewing, setPreviewing] = useState(false);
  const placement = useRef<((distance?: number) => ProjectVector3 | undefined) | undefined>(undefined);
  const placementPosition = useCallback((distance?: number) => placement.current?.(distance), []);
  const setPlacementProvider = useCallback((provider?: (distance?: number) => ProjectVector3 | undefined) => { placement.current = provider; }, []);
  const inFlight = useRef(false);
  const previewInput = useRef<string | undefined>(undefined);
  const [graphInspection, setGraphInspection] = useState<'accepted' | 'rejected'>();
  const [progress, setProgress] = useState<{ completed: number; total: number }>();
  const run = useRef(0), mounted = useRef(true), serial = useRef(0);
  const previousSelection = useRef(project.selection.join(','));
  const reportError = useLogReporter('Occlusion');
  const available = hostAvailable && project.capabilities.includes('editor.occlusion-coverage.query')
    && project.capabilities.includes('editor.occlusion-coverage.update');
  const previewAvailable = available && project.capabilities.includes('editor.occlusion-coverage.preview');
  useEffect(() => {
    mounted.current = true;
    const unsubscribe = window.forge.onOcclusionCoverageProgress(setProgress);
    return () => { mounted.current = false; run.current++; unsubscribe(); void window.forge.cancelOcclusionCoveragePreview().catch(() => {}); };
  }, []);
  useEffect(() => {
    let active = true;
    setLoading(true);
    if (available) void window.forge.getOcclusionCoverage().then(value => {
      if (active) { setSnapshot(value); setLoading(false); }
    }).catch(cause => {
      if (active) { reportError(errorMessage(cause)); setSnapshot(undefined); setLoading(false); }
    });
    else { setSnapshot(undefined); setLoading(false); }
    return () => { active = false; };
  }, [available, project.projectPath, project.lastEventSequence, reportError]);
  useEffect(() => {
    run.current++; void window.forge.cancelOcclusionCoveragePreview().catch(() => {});
    setPreview(undefined); setSelectedId(undefined); setSelectedMarkerId(undefined); setShowVolumes(false); setShowPreview(false);
    setHiddenSources(new Set()); setFocusRequest(undefined); setGraphInspection(undefined);
  }, [project.projectPath, project.projectId]);
  useEffect(() => {
    if (inFlight.current && previewInput.current !== snapshot?.projectFingerprint) {
      run.current++; void window.forge.cancelOcclusionCoveragePreview().catch(() => {});
    }
  }, [snapshot?.projectFingerprint]);
  useEffect(() => {
    const selection = project.selection.join(',');
    if (selection !== previousSelection.current && selection) { setSelectedId(undefined); setSelectedMarkerId(undefined); }
    previousSelection.current = selection;
  }, [project.selection]);
  useEffect(() => {
    if (!snapshot) return;
    const source = snapshot.state.sources.find(source => source.id === selectedId);
    if (selectedId && selectedId !== PRIMARY_OCCLUSION_SOURCE_ID && !source) setSelectedId(undefined);
    if (selectedMarkerId && !source?.graph?.markers.some(marker => marker.id === selectedMarkerId)) setSelectedMarkerId(undefined);
  }, [selectedId, selectedMarkerId, snapshot]);

  useEffect(() => { setShowPreview(false); }, [snapshot?.state.primaryEnabled]);

  const commit = async (state: OcclusionCoverageState, fingerprint = snapshot?.projectFingerprint): Promise<boolean> => {
    if (!fingerprint || loading || busy) return false;
    setUpdating(true);
    setLoading(true);
    try {
      const value = await update(fingerprint, state);
      if (mounted.current) setSnapshot(value);
      return true;
    } catch (cause) {
      if (mounted.current) {
        reportError(errorMessage(cause));
        try { const value = await window.forge.getOcclusionCoverage(); if (mounted.current) setSnapshot(value); }
        catch { if (mounted.current) setSnapshot(undefined); }
      }
      return false;
    } finally { if (mounted.current) { setLoading(false); setUpdating(false); } }
  };
  const startPreview = useCallback(async () => {
    if (!previewAvailable || !snapshot || inFlight.current || loading || busy) return false;
    inFlight.current = true;
    previewInput.current = snapshot.projectFingerprint;
    const generation = ++run.current;
    setPreviewing(true); setProgress({ completed: 0, total: 1 });
    try {
      const value = await window.forge.previewOcclusionCoverage(snapshot.projectFingerprint);
      if (mounted.current && generation === run.current) {
        setPreview(value); setShowPreview(true); if (!value.graphPreview) setGraphInspection(undefined);
        return true;
      }
      return false;
    } catch (cause) { if (mounted.current && generation === run.current) reportError(errorMessage(cause)); return false; }
    finally { inFlight.current = false; if (mounted.current) setPreviewing(false); }
  }, [snapshot, previewAvailable, loading, busy, project.projectId, project.projectPath, project.lastEventSequence, reportError]);
  const setSourceVisible = (id: string, visible: boolean) => setHiddenSources(old => {
    const next = new Set(old); if (visible) next.delete(id); else next.add(id); return next;
  });
  const select = (id?: string, markerId?: string) => {
    setSelectedId(id); setSelectedMarkerId(id ? markerId : undefined);
    if (id) { setShowVolumes(true); setSourceVisible(id, true); clearEntitySelection(); }
  };
  return { snapshot, available, loading, updating, selectedId, selectedMarkerId, select, showVolumes, setShowVolumes, hiddenSources,
    setSourceVisible, focusRequest, focus: (id, markerId) => { select(id, markerId); setFocusRequest({ id, markerId, serial: ++serial.current }); },
    focusHandled: () => setFocusRequest(undefined), commit, placementPosition, setPlacementProvider, previewAvailable, preview, previewing,
    previewStale: Boolean(preview && preview.projectFingerprint !== snapshot?.projectFingerprint),
    progress, startPreview, showPreview, setShowPreview, graphInspection, setGraphInspection,
    cancelPreview: () => { run.current++; void window.forge.cancelOcclusionCoveragePreview().catch(cause => { if (mounted.current) reportError(errorMessage(cause)); }); } };
}
