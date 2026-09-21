import { useEffect, useRef } from 'react';
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import type { EditorLoadProgress, EditorTerrainSource } from '../../types/ForgeApi.js';
import {
  disposeObject,
  frameObject,
  framePs2Positions,
  ps2PositionToScene,
  rotateCamera,
  updateCameraFlight,
  updateCameraMovement,
} from '../../utils/Scene.ts';
import type { CameraFlight } from '../../utils/Scene.ts';
import { nextViewportSelection } from './EditorPanelState.ts';
import { SceneProjection } from './SceneProjection.ts';

interface SceneViewportProps {
  entities: readonly EditorEntity[];
  focusEntityId?: string;
  selection: readonly string[];
  terrain?: EditorTerrainSource;
  onFocusHandled(): void;
  onLoadProgress(progress?: EditorLoadProgress): void;
  onSelectionChange(values: string[]): void;
}

export function SceneViewport({
  entities,
  focusEntityId,
  selection,
  terrain: terrainSource,
  onFocusHandled,
  onLoadProgress,
  onSelectionChange,
}: SceneViewportProps) {
  const container = useRef<HTMLDivElement>(null);
  const currentSelection = useRef(selection);
  const currentEntities = useRef(entities);
  const focusHandled = useRef(onFocusHandled);
  const loadProgressChanged = useRef(onLoadProgress);
  const selectionChanged = useRef(onSelectionChange);
  currentSelection.current = selection;
  currentEntities.current = entities;
  focusHandled.current = onFocusHandled;
  loadProgressChanged.current = onLoadProgress;
  selectionChanged.current = onSelectionChange;
  const viewport = useRef<{
    projection: SceneProjection;
    camera: THREE.PerspectiveCamera;
    controls: OrbitControls;
    content: THREE.Group;
    terrain: THREE.Group;
    velocity: THREE.Vector3;
    flight?: CameraFlight;
    framed: boolean;
  }>(null);

  useEffect(() => {
    const element = container.current;
    if (!element) return;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x151922);
    scene.add(new THREE.HemisphereLight(0xffffff, 0x5c6370, 2));
    const content = new THREE.Group();
    content.name = 'Forge scene content';
    const terrain = new THREE.Group();
    terrain.name = 'UYA terrain';
    const currentProjection = new SceneProjection();
    content.add(terrain, currentProjection.root);
    scene.add(content);

    const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 80_000);

    const renderer = new THREE.WebGLRenderer({ antialias: true });
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
    viewport.current = { projection: currentProjection, camera, controls, content, terrain, velocity, framed: false };

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

    renderer.setAnimationLoop(() => {
      const delta = Math.min(clock.getDelta(), 0.05);
      updateCameraMovement(camera, controls.target, movement, velocity, delta);
      const flight = viewport.current?.flight;
      if (flight && updateCameraFlight(camera.position, controls.target, flight, delta) && viewport.current)
        viewport.current.flight = undefined;
      controls.update();
      renderer.render(scene, camera);
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
    let disposed = false;
    const loadedScenes: THREE.Object3D[] = [];
    const total = terrainSource.urls.length + terrainSource.assets.length;
    let completed = 0;
    const reportLoaded = () => {
      completed += 1;
      if (!disposed) loadProgressChanged.current({
        status: 'loading', label: 'Loading terrain and entity meshes…', completed, total,
      });
    };
    loadProgressChanged.current({
      status: 'loading', label: 'Loading terrain and entity meshes…', completed, total,
    });
    void Promise.resolve().then(async () => {
      if (!terrainSource.urls.length) throw new Error('The render package contains no terrain');
      const loader = new GLTFLoader();
      const [terrainResults, assetResults] = await Promise.all([
        Promise.allSettled(terrainSource.urls.map((url) => loader.loadAsync(url).finally(reportLoaded))),
        Promise.allSettled(terrainSource.assets.map((asset) =>
          (asset.url ? loader.loadAsync(asset.url) : Promise.reject(new Error(asset.error ?? 'Asset has no render payload')))
            .finally(reportLoaded))),
      ]);
      const loaded = terrainResults.flatMap((result) => result.status === 'fulfilled' ? [result.value.scene] : []);
      if (disposed || !viewport.current) {
        for (const scene of loaded) disposeObject(scene);
        for (const result of assetResults) if (result.status === 'fulfilled') disposeObject(result.value.scene);
        return;
      }
      if (!loaded.length) throw terrainResults.find((result) => result.status === 'rejected')?.reason
        ?? new Error('Could not load terrain');
      for (const scene of loaded) {
        scene.traverse((object) => { if (/^lod_[1-9]/i.test(object.name)) object.visible = false; });
        viewport.current.terrain.add(scene);
        loadedScenes.push(scene);
      }
      const templates = new Map<string, THREE.Object3D>();
      const failedAssets = new Set<string>();
      assetResults.forEach((result, index) => {
        const asset = terrainSource.assets[index];
        if (result.status === 'fulfilled') {
          templates.set(asset.assetId, result.value.scene);
          loadedScenes.push(result.value.scene);
        } else {
          failedAssets.add(asset.assetId);
        }
      });
      viewport.current.projection.setAssetTemplates(templates, failedAssets);
      viewport.current.projection.sync(currentEntities.current, currentSelection.current);
      if (!frameEntities(viewport.current.camera, viewport.current.controls, currentEntities.current))
        frameObject(viewport.current.camera, viewport.current.controls, viewport.current.content);
      viewport.current.framed = true;
      const terrainFailures = terrainResults.length - loaded.length;
      const assetFailures = failedAssets.size;
      const firstAssetFailure = assetResults.findIndex((result) => result.status === 'rejected');
      const firstAssetError = firstAssetFailure < 0 ? '' : terrainSource.assets[firstAssetFailure].error
        ?? String((assetResults[firstAssetFailure] as PromiseRejectedResult).reason);
      const failures = [
        terrainFailures && `${terrainFailures} terrain section${terrainFailures === 1 ? '' : 's'}`,
        assetFailures && `${assetFailures} asset${assetFailures === 1 ? '' : 's'}`,
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
      for (const scene of loadedScenes) disposeObject(scene);
    };
  }, [terrainSource]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    current.projection.sync(entities, selection);
    if (!current.framed && entities.length) {
      frameEntities(current.camera, current.controls, entities);
      current.framed = true;
    }
  }, [entities, selection]);

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
    </div>
  );
}

const MOVEMENT_KEYS = new Set(['KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyQ', 'KeyE', 'Space', 'ShiftLeft', 'ShiftRight']);

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
