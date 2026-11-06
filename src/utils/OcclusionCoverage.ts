import type { ProjectTransform, ProjectVector3 } from '../types/EditorRuntime.js';
import type { OcclusionCoverageMarker, OcclusionCoverageSnapshot, OcclusionCoverageSource, OcclusionCoverageState } from '../types/OcclusionCoverage.js';
import type { OcclusionVisibilityData } from '../types/Occlusion.js';

export const PRIMARY_OCCLUSION_SOURCE_ID = 'primary';

export const OCCLUSION_COVERAGE_LIMITS = { sources: 1024, markersPerGraph: 4096, markers: 16384, extent: 1_000_000 };

export function needsOcclusionCoverageGeneration(state?: OcclusionCoverageState): boolean {
  return Boolean(state?.sources.some(source => source.enabled));
}

export function isOcclusionCoverageDisabled(state?: OcclusionCoverageState): boolean {
  return Boolean(state && !state.primaryEnabled && !needsOcclusionCoverageGeneration(state));
}

export function isOcclusionVisibilityCurrent(data: OcclusionVisibilityData | undefined, coverage?: OcclusionCoverageSnapshot): boolean {
  if (!data) return false;
  if (isOcclusionCoverageDisabled(coverage?.state)) return data.octants.length === 0;
  if (data.coverageRecipeFingerprint || data.projectFingerprint)
    return data.source === 'generated' && data.coverageRecipeFingerprint === coverage?.recipeFingerprint
      && data.projectFingerprint === coverage?.visibilityFingerprint;
  return !needsOcclusionCoverageGeneration(coverage?.state);
}

export function createOcclusionCoverageSource(kind: 'box' | 'cylinder', name: string,
  position = { x: 40, y: 40, z: 20 }): OcclusionCoverageSource {
  return {
    id: crypto.randomUUID(), name, enabled: true, role: 'include',
    transform: { position: { ...position }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 } },
    ...(kind === 'box' ? { boxSize: { x: 8, y: 8, z: 8 } } : { cylinder: { radius: 4, height: 8 } }),
  };
}

export function createOcclusionCoverageMarker(name: string, position = { x: 0, y: 0, z: 0 }): OcclusionCoverageMarker {
  return { id: crypto.randomUUID(), name, position: { ...position } };
}

export function occlusionSeedLabel(index: number): string { return `Seed ${index + 1}`; }

export function hasOcclusionSeedGraph(source: OcclusionCoverageSource): boolean { return Boolean(source.graph && !source.graph.disabled); }

export function occlusionVolumeSize(source: OcclusionCoverageSource): ProjectVector3 {
  return source.boxSize ?? (source.cylinder ? { x: source.cylinder.radius * 2, y: source.cylinder.radius * 2, z: source.cylinder.height } : source.graph!.size!);
}

export function occlusionVolumeContains(source: OcclusionCoverageSource, position: ProjectVector3): boolean {
  const size = occlusionVolumeSize(source);
  return Math.abs(position.z) <= size.z / 2 && (source.cylinder
    ? Math.hypot(position.x, position.y) <= source.cylinder.radius
    : Math.abs(position.x) <= size.x / 2 && Math.abs(position.y) <= size.y / 2);
}

export function clampOcclusionSeed(source: OcclusionCoverageSource, position: ProjectVector3): ProjectVector3 {
  const size = occlusionVolumeSize(source);
  const result = { x: Math.max(-size.x / 2, Math.min(size.x / 2, position.x)),
    y: Math.max(-size.y / 2, Math.min(size.y / 2, position.y)), z: Math.max(-size.z / 2, Math.min(size.z / 2, position.z)) };
  if (source.cylinder) {
    const distance = Math.hypot(result.x, result.y);
    if (distance > source.cylinder.radius) {
      const scale = source.cylinder.radius / distance * (1 - 1e-7);
      result.x *= scale; result.y *= scale;
    }
  }
  return result;
}

export function setOcclusionSeedGraph(source: OcclusionCoverageSource, enabled: boolean): OcclusionCoverageSource {
  const graph = source.graph ?? { markers: [], sticky: true, stickyDistance: 8 };
  const { size, ...settings } = graph;
  return { ...source, ...(size ? { boxSize: size } : {}), graph: { ...settings, disabled: !enabled } };
}

export function setOcclusionVolumeShape(source: OcclusionCoverageSource, kind: 'box' | 'cylinder'): OcclusionCoverageSource {
  const size = occlusionVolumeSize(source);
  const result: OcclusionCoverageSource = { ...source, boxSize: undefined, cylinder: undefined };
  if (result.graph) result.graph = { ...result.graph, size: undefined };
  const radius = Math.max(size.x, size.y) / 2;
  const seedRadius = Math.max(0, ...(source.graph?.markers.map(m => Math.hypot(m.position.x, m.position.y)) ?? []));
  return { ...result, ...(kind === 'box' ? { boxSize: size } : { cylinder: { radius: Math.max(radius, seedRadius * (1 + 1e-7)), height: size.z } }) };
}

export function transformOcclusionCoverageSource(source: OcclusionCoverageSource,
  transform: ProjectTransform, axis?: string | null): OcclusionCoverageSource | undefined {
  const scale = { ...transform.scale };
  if (Object.values(scale).some(value => !Number.isFinite(value) || value <= 0)) return undefined;
  if (source.cylinder) {
    // Scene Y is native Z (height); either scene radial handle changes both native radii.
    const radial = axis?.includes('X') ? scale.x : axis?.includes('Z') ? scale.y : axis === 'Y' ? 1
      : Math.abs(scale.x - 1) >= Math.abs(scale.y - 1) ? scale.x : scale.y;
    scale.x = radial; scale.y = radial;
  }
  const result: OcclusionCoverageSource = { ...source, transform: {
    position: { ...transform.position }, rotation: { ...transform.rotation }, scale: { x: 1, y: 1, z: 1 },
  } };
  if (source.boxSize) result.boxSize = { x: source.boxSize.x * scale.x, y: source.boxSize.y * scale.y, z: source.boxSize.z * scale.z };
  if (source.cylinder) result.cylinder = { radius: source.cylinder.radius * scale.x, height: source.cylinder.height * scale.z };
  if (source.graph?.size) result.graph = { ...source.graph,
    size: { x: source.graph.size.x * scale.x, y: source.graph.size.y * scale.y, z: source.graph.size.z * scale.z } };
  return isOcclusionCoverageState({ schemaVersion: 1, primaryEnabled: false, sources: [result] }) ? result : undefined;
}

export function duplicateOcclusionCoverageSource(source: OcclusionCoverageSource): OcclusionCoverageSource {
  const copy = structuredClone(source); copy.id = crypto.randomUUID(); copy.name = `${source.name} copy`.slice(0, 256);
  for (const marker of copy.graph?.markers ?? []) marker.id = crypto.randomUUID();
  return copy;
}

export function isOcclusionCoverageState(value: unknown): value is OcclusionCoverageState {
  if (!record(value, ['schemaVersion', 'primaryEnabled', 'sources']) || value.schemaVersion !== 1
    || typeof value.primaryEnabled !== 'boolean' || !Array.isArray(value.sources)
    || value.sources.length > OCCLUSION_COVERAGE_LIMITS.sources) return false;
  const ids = new Set<string>();
  let markers = 0;
  const identifier = (id: unknown) => {
    if (typeof id !== 'string' || !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(id)
      || id === '00000000-0000-0000-0000-000000000000' || ids.has(id)) return false;
    ids.add(id); return true;
  };
  for (const source of value.sources) {
    if (!record(source, ['id', 'name', 'enabled', 'role', 'transform', 'boxSize', 'cylinder', 'graph'])
      || !identifier(source.id) || !name(source.name) || typeof source.enabled !== 'boolean'
      || (source.role !== 'include' && source.role !== 'exclude') || !pose(source.transform)) return false;
    const graph = source.graph;
    if (graph !== undefined && !record(graph, ['size', 'markers', 'sticky', 'stickyDistance', 'disabled'])) return false;
    if ([source.boxSize, source.cylinder, graph?.size].filter(v => v !== undefined).length !== 1) return false;
    if (source.boxSize !== undefined && !size(source.boxSize)) return false;
    if (source.cylinder !== undefined && (!record(source.cylinder, ['radius', 'height'])
      || !dimension(source.cylinder.radius) || !dimension(source.cylinder.height))) return false;
    if (graph !== undefined) {
      if ((graph.size !== undefined && (!size(graph.size) || graph.disabled))
        || (graph.disabled !== undefined && typeof graph.disabled !== 'boolean')
        || (source.role !== 'include' && !graph.disabled)
        || (graph.sticky !== undefined && typeof graph.sticky !== 'boolean')
        || (graph.stickyDistance !== undefined && (!coordinate(graph.stickyDistance) || graph.stickyDistance < 0))
        || !Array.isArray(graph.markers) || graph.markers.length > OCCLUSION_COVERAGE_LIMITS.markersPerGraph
        || (markers += graph.markers.length) > OCCLUSION_COVERAGE_LIMITS.markers) return false;
      for (const marker of graph.markers) {
        if (!record(marker, ['id', 'name', 'position']) || !identifier(marker.id) || !name(marker.name)
          || !vector(marker.position) || !occlusionVolumeContains(source as unknown as OcclusionCoverageSource, marker.position)) return false;
      }
    }
  }
  return true;
}

export function isOcclusionCoverageFingerprint(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
}

function record(value: unknown, keys: string[]): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    && Object.keys(value).every(key => keys.includes(key));
}

function name(value: unknown): boolean { return typeof value === 'string' && value.trim().length > 0 && value.length <= 256; }

function coordinate(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && Math.abs(value) <= OCCLUSION_COVERAGE_LIMITS.extent;
}

function dimension(value: unknown): value is number { return coordinate(value) && Math.fround(value) > 0; }

function vector(value: unknown): value is { x: number; y: number; z: number } {
  return record(value, ['x', 'y', 'z']) && coordinate(value.x) && coordinate(value.y) && coordinate(value.z);
}

function size(value: unknown): value is { x: number; y: number; z: number } {
  return vector(value) && dimension(value.x) && dimension(value.y) && dimension(value.z);
}

function pose(value: unknown): boolean {
  if (!record(value, ['position', 'rotation', 'scale']) || !vector(value.position)
    || !vector(value.scale) || value.scale.x !== 1 || value.scale.y !== 1 || value.scale.z !== 1
    || !record(value.rotation, ['x', 'y', 'z', 'w'])) return false;
  const q = value.rotation;
  return coordinate(q.x) && coordinate(q.y) && coordinate(q.z) && coordinate(q.w)
    && Math.abs(q.x ** 2 + q.y ** 2 + q.z ** 2 + q.w ** 2 - 1) <= 0.0001;
}
