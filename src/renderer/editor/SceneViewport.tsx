import { Checkbox, Group, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import type { EditorLoadProgress, EditorTerrainSource } from '../../types/ForgeApi.js';
import {
  disposeObject,
  applySceneEnvironment,
  configureSkybox,
  frameObject,
  framePs2Positions,
  ps2PositionToScene,
  rotateCamera,
  updateCameraFlight,
  updateCameraMovement,
} from '../../utils/Scene.ts';
import type { CameraFlight } from '../../utils/Scene.ts';
import { configurePs2MaterialAlpha } from '../../utils/Ps2Materials.ts';
import { nextViewportSelection } from './EditorPanelState.ts';
import { SceneProjection } from './SceneProjection.ts';

interface SceneViewportProps {
  entities: readonly EditorEntity[];
  focusEntityId?: string;
  selection: readonly string[];
  terrain?: EditorTerrainSource;
  onFocusHandled(): void;
  onLoadProgress(progress?: EditorLoadProgress): void;
  onSkyPiecesChange(values: string[]): void;
  onSelectionChange(values: string[]): void;
}

export function SceneViewport({
  entities,
  focusEntityId,
  selection,
  terrain: terrainSource,
  onFocusHandled,
  onLoadProgress,
  onSkyPiecesChange,
  onSelectionChange,
}: SceneViewportProps) {
  const container = useRef<HTMLDivElement>(null);
  const [visibility, setVisibility] = useState(DEFAULT_VISIBILITY);
  const [stats, setStats] = useState<ViewportStats>();
  const currentVisibility = useRef(visibility);
  const currentSelection = useRef(selection);
  const currentEntities = useRef(entities);
  const focusHandled = useRef(onFocusHandled);
  const loadProgressChanged = useRef(onLoadProgress);
  const selectionChanged = useRef(onSelectionChange);
  currentSelection.current = selection;
  currentEntities.current = entities;
  currentVisibility.current = visibility;
  focusHandled.current = onFocusHandled;
  loadProgressChanged.current = onLoadProgress;
  selectionChanged.current = onSelectionChange;
  const viewport = useRef<{
    projection: SceneProjection;
    scene: THREE.Scene;
    skyScene: THREE.Scene;
    camera: THREE.PerspectiveCamera;
    controls: OrbitControls;
    content: THREE.Group;
    terrain: THREE.Group;
    sky: THREE.Group;
    skyEye: THREE.Vector3;
    velocity: THREE.Vector3;
    flight?: CameraFlight;
    framed: boolean;
  }>(null);

  useEffect(() => {
    const element = container.current;
    if (!element) return;

    const scene = new THREE.Scene();
    const skyScene = new THREE.Scene();
    applySceneEnvironment(scene, undefined, skyScene);
    scene.add(new THREE.HemisphereLight(0xffffff, 0x5c6370, 2));
    const content = new THREE.Group();
    content.name = 'Forge scene content';
    const terrain = new THREE.Group();
    terrain.name = 'UYA terrain';
    const sky = new THREE.Group();
    sky.name = 'UYA sky';
    const currentProjection = new SceneProjection();
    content.add(terrain, currentProjection.root);
    skyScene.add(sky);
    scene.add(content);

    const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 80_000);

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.autoClear = false;
    renderer.info.autoReset = false;
    renderer.setPixelRatio(1);
    element.append(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.enableRotate = false;
    controls.zoomSpeed = 0.35;
    const movement = new Set<string>();
    const velocity = new THREE.Vector3();
    const clock = new THREE.Clock();
    let cameraInputActive = false;
    camera.position.set(0, 150, 300);
    controls.update();
    viewport.current = {
      projection: currentProjection,
      scene,
      skyScene,
      camera,
      controls,
      content,
      terrain,
      sky,
      skyEye: new THREE.Vector3(),
      velocity,
      framed: false,
    };

    const keyDown = (event: KeyboardEvent) => {
      if (!cameraInputActive || !MOVEMENT_KEYS.has(event.code)) return;
      if (viewport.current) viewport.current.flight = undefined;
      movement.add(event.code);
      event.preventDefault();
    };
    const keyUp = (event: KeyboardEvent) => movement.delete(event.code);
    const deactivateCameraInput = () => {
      cameraInputActive = false;
      movement.clear();
    };
    const activateCameraInput = () => { cameraInputActive = true; };
    window.addEventListener('keydown', keyDown);
    window.addEventListener('keyup', keyUp);
    window.addEventListener('blur', deactivateCameraInput);
    renderer.domElement.addEventListener('pointerenter', activateCameraInput);
    renderer.domElement.addEventListener('pointerleave', deactivateCameraInput);
    renderer.domElement.addEventListener('focus', activateCameraInput);
    renderer.domElement.addEventListener('blur', deactivateCameraInput);
    renderer.domElement.tabIndex = 0;

    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    let pointerStart: { x: number; y: number } | undefined;
    let lookPointerId: number | undefined;
    let lookPosition: { x: number; y: number } | undefined;
    const pointerDown = (event: PointerEvent) => {
      if (viewport.current) viewport.current.flight = undefined;
      renderer.domElement.focus({ preventScroll: true });
      if (event.button === 0) {
        pointerStart = { x: event.clientX, y: event.clientY };
        lookPosition = pointerStart;
        lookPointerId = event.pointerId;
        renderer.domElement.setPointerCapture(event.pointerId);
      }
    };
    const pointerMove = (event: PointerEvent) => {
      if (event.pointerId !== lookPointerId || !lookPosition) return;
      rotateCamera(camera, controls.target, event.clientX - lookPosition.x, event.clientY - lookPosition.y);
      lookPosition = { x: event.clientX, y: event.clientY };
    };
    const pointerUp = (event: PointerEvent) => {
      if (event.button !== 0 || !pointerStart) return;
      lookPointerId = undefined;
      lookPosition = undefined;
      if (renderer.domElement.hasPointerCapture(event.pointerId)) renderer.domElement.releasePointerCapture(event.pointerId);
      const distance = Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y);
      pointerStart = undefined;
      if (distance > 4) return;
      const bounds = renderer.domElement.getBoundingClientRect();
      pointer.set(
        (event.clientX - bounds.left) / bounds.width * 2 - 1,
        -(event.clientY - bounds.top) / bounds.height * 2 + 1,
      );
      raycaster.setFromCamera(pointer, camera);
      const id = currentProjection.resolvePick(raycaster.intersectObject(currentProjection.root, true));
      const next = nextViewportSelection(currentSelection.current, id, {
        toggle: event.ctrlKey || event.metaKey,
        add: event.shiftKey,
      });
      if (next.length === currentSelection.current.length
        && next.every((value, index) => value === currentSelection.current[index])) return;
      currentSelection.current = next;
      selectionChanged.current(next);
    };
    const pointerCancel = (event: PointerEvent) => {
      if (event.pointerId !== lookPointerId) return;
      pointerStart = undefined;
      lookPointerId = undefined;
      lookPosition = undefined;
      if (renderer.domElement.hasPointerCapture(event.pointerId)) renderer.domElement.releasePointerCapture(event.pointerId);
    };
    renderer.domElement.addEventListener('pointerdown', pointerDown);
    renderer.domElement.addEventListener('pointermove', pointerMove);
    renderer.domElement.addEventListener('pointerup', pointerUp);
    renderer.domElement.addEventListener('pointercancel', pointerCancel);
    renderer.domElement.addEventListener('contextmenu', preventDefault);

    const resize = () => {
      const { clientWidth, clientHeight } = element;
      camera.aspect = clientWidth / Math.max(clientHeight, 1);
      camera.updateProjectionMatrix();
      renderer.setSize(clientWidth, clientHeight, false);
    };

    const observer = new ResizeObserver(resize);
    observer.observe(element);
    resize();

    let statsStarted = performance.now();
    let statsFrames = 0;
    renderer.setAnimationLoop(() => {
      const delta = Math.min(clock.getDelta(), 0.05);
      updateCameraMovement(camera, controls.target, movement, velocity, delta);
      const flight = viewport.current?.flight;
      if (flight && updateCameraFlight(camera.position, controls.target, flight, delta) && viewport.current)
        viewport.current.flight = undefined;
      sky.position.copy(camera.position).sub(viewport.current?.skyEye ?? ZERO_VECTOR);
      controls.update();
      renderer.info.reset();
      renderer.clear();
      renderer.render(skyScene, camera);
      renderer.clearDepth();
      renderer.render(scene, camera);
      const now = performance.now();
      if (currentVisibility.current.stats) {
        statsFrames += 1;
        if (now - statsStarted >= 500) {
          setStats({
            fps: Math.round(statsFrames * 1_000 / (now - statsStarted)),
            calls: renderer.info.render.calls,
            triangles: renderer.info.render.triangles,
          });
          statsFrames = 0;
          statsStarted = now;
        }
      } else {
        statsFrames = 0;
        statsStarted = now;
      }
    });

    return () => {
      observer.disconnect();
      renderer.domElement.removeEventListener('pointerdown', pointerDown);
      renderer.domElement.removeEventListener('pointermove', pointerMove);
      renderer.domElement.removeEventListener('pointerup', pointerUp);
      renderer.domElement.removeEventListener('pointercancel', pointerCancel);
      renderer.domElement.removeEventListener('contextmenu', preventDefault);
      window.removeEventListener('keydown', keyDown);
      window.removeEventListener('keyup', keyUp);
      window.removeEventListener('blur', deactivateCameraInput);
      renderer.domElement.removeEventListener('pointerenter', activateCameraInput);
      renderer.domElement.removeEventListener('pointerleave', deactivateCameraInput);
      renderer.domElement.removeEventListener('focus', activateCameraInput);
      renderer.domElement.removeEventListener('blur', deactivateCameraInput);
      renderer.setAnimationLoop(null);
      controls.dispose();
      sky.removeFromParent();
      sky.clear();
      terrain.removeFromParent();
      terrain.clear();
      currentProjection.dispose();
      if (viewport.current?.projection === currentProjection) viewport.current = null;
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
    };
  }, []);

  useEffect(() => {
    if (!terrainSource) return;
    onSkyPiecesChange([]);
    let disposed = false;
    const loadedScenes: THREE.Object3D[] = [];
    const total = terrainSource.urls.length + terrainSource.assets.length + (terrainSource.skyUrl ? 1 : 0);
    let completed = 0;
    const reportLoaded = () => {
      completed += 1;
      if (!disposed) loadProgressChanged.current({
        status: 'loading', label: 'Loading terrain, sky, and entity meshes…', completed, total,
      });
    };
    loadProgressChanged.current({
      status: 'loading', label: 'Loading terrain, sky, and entity meshes…', completed, total,
    });
    applySceneEnvironment(viewport.current!.scene, terrainSource.environment, viewport.current!.skyScene);
    void Promise.resolve().then(async () => {
      if (!terrainSource.urls.length) throw new Error('The render package contains no terrain');
      const loader = new GLTFLoader();
      const [terrainResults, assetResults, skyResults] = await Promise.all([
        Promise.allSettled(terrainSource.urls.map((url) => loader.loadAsync(url).finally(reportLoaded))),
        Promise.allSettled(terrainSource.assets.map((asset) =>
          (asset.url ? loader.loadAsync(asset.url) : Promise.reject(new Error(asset.error ?? 'Asset has no render payload')))
            .finally(reportLoaded))),
        Promise.allSettled(terrainSource.skyUrl
          ? [loader.loadAsync(terrainSource.skyUrl).finally(reportLoaded)]
          : []),
      ]);
      const loaded = terrainResults.flatMap((result) => result.status === 'fulfilled' ? [result.value.scene] : []);
      if (disposed || !viewport.current) {
        for (const scene of loaded) disposeObject(scene);
        for (const result of assetResults) if (result.status === 'fulfilled') disposeObject(result.value.scene);
        for (const result of skyResults) if (result.status === 'fulfilled') disposeObject(result.value.scene);
        return;
      }
      if (!loaded.length) throw terrainResults.find((result) => result.status === 'rejected')?.reason
        ?? new Error('Could not load terrain');
      for (const scene of loaded) {
        configurePs2MaterialAlpha(scene, 'tfrag');
        scene.traverse((object) => { if (/^lod_[1-9]/i.test(object.name)) object.visible = false; });
        viewport.current.terrain.add(scene);
        loadedScenes.push(scene);
      }
      if (skyResults[0]?.status === 'fulfilled') {
        const skyScene = skyResults[0].value.scene;
        configurePs2MaterialAlpha(skyScene, 'sky');
        const sky = configureSkybox(skyScene);
        viewport.current.skyEye.copy(sky.eye);
        onSkyPiecesChange(sky.pieces);
        viewport.current.sky.add(skyScene);
        viewport.current.sky.visible = currentVisibility.current.sky;
        loadedScenes.push(skyScene);
      }
      const templates = new Map<string, THREE.Object3D>();
      const failedAssets = new Set<string>();
      assetResults.forEach((result, index) => {
        const asset = terrainSource.assets[index];
        if (result.status === 'fulfilled') {
          configurePs2MaterialAlpha(result.value.scene, asset.kind);
          templates.set(asset.assetId, result.value.scene);
          loadedScenes.push(result.value.scene);
        } else {
          failedAssets.add(asset.assetId);
        }
      });
      viewport.current.projection.setAssetTemplates(templates, failedAssets);
      viewport.current.projection.sync(
        currentEntities.current,
        currentSelection.current,
        visibleEntityLayers(currentEntities.current, currentVisibility.current),
        currentVisibility.current.markers,
      );
      if (!frameEntities(viewport.current.camera, viewport.current.controls, currentEntities.current))
        frameObject(viewport.current.camera, viewport.current.controls, viewport.current.content);
      viewport.current.framed = true;
      const terrainFailures = terrainResults.length - loaded.length;
      const skyFailures = skyResults.filter((result) => result.status === 'rejected').length;
      const firstAssetFailure = assetResults.findIndex((result) => result.status === 'rejected');
      const firstAssetError = firstAssetFailure < 0 ? '' : terrainSource.assets[firstAssetFailure].error
        ?? String((assetResults[firstAssetFailure] as PromiseRejectedResult).reason);
      const failures = [
        terrainFailures && `${terrainFailures} terrain section${terrainFailures === 1 ? '' : 's'}`,
        skyFailures && 'sky',
        ...failedAssetFamilies(terrainSource, assetResults),
      ].filter(Boolean);
      loadProgressChanged.current(failures.length ? {
        status: 'error', completed, total,
        label: `${failures.join(' and ')} failed to load; placeholders remain visible.${firstAssetError ? ` ${firstAssetError}` : ''}`,
      } : undefined);
    }).catch((error: unknown) => {
      if (!disposed) loadProgressChanged.current({
        status: 'error', completed, total,
        label: error instanceof Error ? error.message : 'Could not load terrain',
      });
    });
    return () => {
      disposed = true;
      viewport.current?.projection.setAssetTemplates(new Map());
      if (viewport.current) {
        viewport.current.skyEye.set(0, 0, 0);
        applySceneEnvironment(viewport.current.scene, undefined, viewport.current.skyScene);
      }
      for (const scene of loadedScenes) disposeObject(scene);
    };
  }, [onSkyPiecesChange, terrainSource]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    current.projection.sync(
      entities,
      selection,
      visibleEntityLayers(entities, currentVisibility.current),
      currentVisibility.current.markers,
    );
    if (!current.framed && entities.length) {
      frameEntities(current.camera, current.controls, entities);
      current.framed = true;
    }
  }, [entities, selection]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    current.terrain.visible = visibility.terrain;
    current.sky.visible = visibility.sky;
    current.projection.sync(
      currentEntities.current,
      currentSelection.current,
      visibleEntityLayers(currentEntities.current, visibility),
      visibility.markers,
    );
    if (!visibility.stats) setStats(undefined);
  }, [visibility]);

  useEffect(() => {
    if (!focusEntityId) return;
    const current = viewport.current;
    const entity = currentEntities.current.find((value) => value.id === focusEntityId);
    focusHandled.current();
    if (!current || !entity) return;
    const sphere = current.projection.getBounds(entity.id)?.getBoundingSphere(new THREE.Sphere());
    const center = sphere?.center ?? ps2PositionToScene(entity.transform.position);
    const radius = Math.max(sphere?.radius ?? 8, 1);
    const direction = current.camera.position.clone().sub(current.controls.target);
    if (direction.lengthSq() < 0.0001) direction.set(1, 0.7, 1);
    current.velocity.set(0, 0, 0);
    current.flight = {
      cameraStart: current.camera.position.clone(),
      cameraEnd: center.clone().addScaledVector(direction.normalize(), Math.max(radius * 2.5, 12)),
      targetStart: current.controls.target.clone(),
      targetEnd: center.clone(),
      elapsed: 0,
      duration: 0.35,
    };
  }, [focusEntityId]);

  return (
    <div aria-label="3D scene viewport" className="scene-viewport">
      <div className="scene-canvas" ref={container} />
      <Group aria-label="Viewport visibility" className="scene-toolbar" gap="xs" role="group">
        {VISIBILITY_CONTROLS.map(([key, label]) => <Checkbox
          checked={visibility[key]}
          key={key}
          label={label}
          size="xs"
          onChange={(event) => setVisibility((value) => ({ ...value, [key]: event.currentTarget.checked }))}
        />)}
        {visibility.stats && <Text c="dimmed" size="xs">
          {stats ? `${stats.fps} FPS · ${stats.calls} calls · ${stats.triangles.toLocaleString()} tris` : 'Measuring…'}
        </Text>}
      </Group>
    </div>
  );
}

const MOVEMENT_KEYS = new Set(['KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyQ', 'KeyE', 'Space', 'ShiftLeft', 'ShiftRight']);
const ZERO_VECTOR = new THREE.Vector3();

interface ViewportVisibility {
  terrain: boolean;
  ties: boolean;
  shrubs: boolean;
  mobys: boolean;
  sky: boolean;
  markers: boolean;
  stats: boolean;
}

interface ViewportStats {
  fps: number;
  calls: number;
  triangles: number;
}

const DEFAULT_VISIBILITY: ViewportVisibility = {
  terrain: true, ties: true, shrubs: true, mobys: true, sky: true, markers: true, stats: false,
};

const VISIBILITY_CONTROLS = [
  ['terrain', 'Terrain'], ['ties', 'Ties'], ['shrubs', 'Shrubs'], ['mobys', 'Mobys'],
  ['sky', 'Sky'], ['markers', 'Markers'], ['stats', 'Stats'],
] as const;

function preventDefault(event: Event): void {
  event.preventDefault();
}

function frameEntities(camera: THREE.PerspectiveCamera, controls: OrbitControls, entities: readonly EditorEntity[]): boolean {
  return framePs2Positions(
    camera,
    controls,
    entities.filter((entity) => !entity.state.hidden && !entity.state.disabled).map((entity) => entity.transform.position),
  );
}

function visibleEntityLayers(
  entities: readonly EditorEntity[],
  visibility: ViewportVisibility,
): ReadonlySet<string> {
  const layers = new Set(entities.map((entity) => entity.layer));
  if (!visibility.ties) layers.delete('ties');
  if (!visibility.shrubs) layers.delete('shrubs');
  if (!visibility.mobys) layers.delete('mobys');
  return layers;
}

function failedAssetFamilies(
  source: EditorTerrainSource,
  results: readonly PromiseSettledResult<unknown>[],
): string[] {
  const counts = new Map<string, number>();
  results.forEach((result, index) => {
    if (result.status === 'fulfilled') return;
    const kind = source.assets[index].kind;
    counts.set(kind, (counts.get(kind) ?? 0) + 1);
  });
  return [...counts].map(([kind, count]) => `${count} ${kind} asset${count === 1 ? '' : 's'}`);
}
