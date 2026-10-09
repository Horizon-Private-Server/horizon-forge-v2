import { useEffect, useRef, useState } from 'react';
import type { DragEvent } from 'react';
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { OutlinePass } from 'three/addons/postprocessing/OutlinePass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';

import type {
  EditorEntity, EditorTransformUpdate, ProjectVector3, ProjectVector4,
} from '../../types/EditorRuntime.js';
import type { AssetModelPlacementDragData, AssetPlacementDragData } from '../../types/AssetExplorer.js';
import type { KeybindingMap } from '../../types/Keybindings.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
import type { CollisionVisualization } from '../../types/CollisionVisualization.js';
import type { EditorLoadProgress, EditorSceneEnvironment, EditorTerrainSource } from '../../types/ForgeApi.js';
import type { EditorSnapSource, EditorSnapTarget } from '../../types/EditorViewport.js';
import type { InstancedCollisionOverlay } from './EditorContext.ts';
import {
  disposeObject,
  applySceneEnvironment,
  frameObject,
  framePs2Positions,
  positionCameraAtPreferredMoby,
  ps2PositionToScene,
  rotateCamera,
  updateCameraFlight,
  updateCameraMovement,
} from '../../utils/Scene.ts';
import type { CameraFlight } from '../../utils/Scene.ts';
import { configureSkybox, skyEyeFromBounds } from '../../utils/SkyboxScene.ts';
import { configureCollisionMaterials } from '../../utils/CollisionMaterials.ts';
import {
  configurePs2AssetVisibility,
  configurePs2MaterialAlpha,
  configurePs2MaterialFog,
} from '../../utils/Ps2Materials.ts';
import { isTextInput } from '../../utils/Dom.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { findKeybindingCommand, transformModeForKeybinding } from '../../utils/Keybindings.ts';
import {
  ASSET_PLACEMENT_MIME, readAssetPlacementDrag, SKY_SHELL_PLACEMENT_MIME,
} from '../../utils/AssetPlacement.ts';
import { nextViewportSelection } from './EditorPanelState.ts';
import { buildGroundPlacement, createGroundSurfaceRaycast } from './ScenePlacement.ts';
import { assetTemplateKey, SceneProjection } from './SceneProjection.ts';
import { resolvePointerSnapTarget } from './SceneSnapping.ts';
import { TransformTool } from './TransformTool.ts';
import type { EditorTransformMode, EditorTransformSpace } from './TransformTool.ts';
import { InstancedCollisionOverlayProjection } from './InstancedCollisionOverlayProjection.ts';
import {
  interpolatePointerSegment, shouldOrbitWhileCollisionPainting,
} from './CollisionPainting.ts';
import { ViewportToolbar } from './ViewportToolbar.tsx';

interface SceneViewportProps {
  entities: readonly EditorEntity[];
  keybindings: KeybindingMap;
  selectionColor: string;
  sceneTreeColors: SceneTreeColors;
  collisionVisualization: CollisionVisualization;
  focusEntityId?: string;
  selection: readonly string[];
  terrain?: EditorTerrainSource;
  environment?: EditorSceneEnvironment;
  disabled: boolean;
  showStats: boolean;
  showOcclusionOctants: boolean;
  showTerrain: boolean;
  showSolidCollision: boolean;
  showPlayerBarriers: boolean;
  instancedCollisionOverlay?: InstancedCollisionOverlay;
  onFocusHandled(): void;
  onLoadProgress(progress?: EditorLoadProgress): void;
  onSolidCollisionVisibilityChange(value: boolean): void;
  onPlayerBarrierVisibilityChange(value: boolean): void;
  onSelectionChange(values: string[]): void;
  onTransformsCommit(values: EditorTransformUpdate[]): Promise<boolean>;
  onSplinePointsCommit(entityId: string, points: ProjectVector4[]): Promise<boolean>;
  onAssetDrop(asset: AssetPlacementDragData, position?: ProjectVector3): Promise<boolean>;
}

interface SkyShellProjection {
  root: THREE.Object3D;
  bounds: THREE.Box3;
  update(deltaSeconds: number): void;
  setRotation(initial: ProjectVector3, velocity: ProjectVector3): void;
}

export function SceneViewport({
  entities,
  keybindings,
  selectionColor,
  sceneTreeColors,
  collisionVisualization,
  focusEntityId,
  selection,
  terrain: terrainSource,
  environment,
  disabled,
  showStats,
  showOcclusionOctants,
  showTerrain,
  showSolidCollision,
  showPlayerBarriers,
  instancedCollisionOverlay,
  onFocusHandled,
  onLoadProgress,
  onSolidCollisionVisibilityChange,
  onPlayerBarrierVisibilityChange,
  onSelectionChange,
  onTransformsCommit,
  onSplinePointsCommit,
  onAssetDrop,
}: SceneViewportProps) {
  const container = useRef<HTMLDivElement>(null);
  const [mode, setMode] = useState<EditorTransformMode>('select');
  const [space, setSpace] = useState<EditorTransformSpace>('world');
  const [snapEnabled, setSnapEnabled] = useState(false);
  const [snapSource, setSnapSource] = useState<EditorSnapSource>('center');
  const [snapTarget, setSnapTarget] = useState<EditorSnapTarget>('grid');
  const [translationSnap, setTranslationSnap] = useState(1);
  const [rotationSnap, setRotationSnap] = useState(15);
  const [scaleSnap, setScaleSnap] = useState(0.1);
  const [notice, setNotice] = useState<string>();
  const [skyDropActive, setSkyDropActive] = useState(false);
  const [stats, setStats] = useState<{ fps: number; calls: number; triangles: number }>();
  const currentShowStats = useRef(showStats);
  const currentSelection = useRef(selection);
  const currentEntities = useRef(entities);
  const currentCollisionVisibility = useRef(collisionVisibility(showSolidCollision, showPlayerBarriers));
  const currentKeybindings = useRef(keybindings);
  const currentEnvironment = useRef(environment);
  const currentCollisionVisualization = useRef(collisionVisualization);
  const currentSelectionColor = useRef(selectionColor);
  const currentInstancedCollisionOverlay = useRef(instancedCollisionOverlay);
  const focusHandled = useRef(onFocusHandled);
  const loadProgressChanged = useRef(onLoadProgress);
  const selectionChanged = useRef(onSelectionChange);
  const transformsCommitted = useRef(onTransformsCommit);
  const splinePointsCommitted = useRef(onSplinePointsCommit);
  const assetDropped = useRef(onAssetDrop);
  currentSelection.current = selection;
  currentEntities.current = entities;
  currentCollisionVisibility.current = collisionVisibility(showSolidCollision, showPlayerBarriers);
  currentKeybindings.current = keybindings;
  currentEnvironment.current = environment;
  currentCollisionVisualization.current = collisionVisualization;
  currentSelectionColor.current = selectionColor;
  currentInstancedCollisionOverlay.current = instancedCollisionOverlay;
  currentShowStats.current = showStats;
  focusHandled.current = onFocusHandled;
  loadProgressChanged.current = onLoadProgress;
  selectionChanged.current = onSelectionChange;
  transformsCommitted.current = onTransformsCommit;
  splinePointsCommitted.current = onSplinePointsCommit;
  assetDropped.current = onAssetDrop;
  const viewport = useRef<{
    projection: SceneProjection;
    scene: THREE.Scene;
    skyScene: THREE.Scene;
    toolScene: THREE.Scene;
    camera: THREE.PerspectiveCamera;
    controls: OrbitControls;
    transformTool: TransformTool;
    outlinePass: OutlinePass;
    content: THREE.Group;
    terrain: THREE.Group;
    occlusion: THREE.Group;
    sky: THREE.Group;
    skyEye: THREE.Vector3;
    updateSky(deltaSeconds: number): void;
    velocity: THREE.Vector3;
    flight?: CameraFlight;
    framed: boolean;
    dropGhost: THREE.Mesh;
    placedTemplates: Map<string, THREE.Object3D>;
    skyShells: Map<string, SkyShellProjection>;
    previewRequests: Set<string>;
    collisionScenes: Set<THREE.Object3D>;
    instancedCollisionPreview: InstancedCollisionOverlayProjection;
  }>(null);
  const dropFrame = useRef<number | undefined>(undefined);
  const pendingDrop = useRef<{ x: number; y: number } | undefined>(undefined);
  const skySourceSignature = entities.filter((entity) => entity.skyShell && entity.asset)
    .map((entity) => `${entity.id}:${entity.asset!.id}:${entity.skyShell!.sourceShellIndex}`)
    .sort().join('|');

  useEffect(() => {
    const element = container.current;
    if (!element) return;

    const scene = new THREE.Scene();
    const skyScene = new THREE.Scene();
    const toolScene = new THREE.Scene();
    applySceneEnvironment(scene, undefined, skyScene);
    scene.add(new THREE.HemisphereLight(0xffffff, 0x5c6370, 2));
    const content = new THREE.Group();
    content.name = 'Forge scene content';
    const terrain = new THREE.Group();
    terrain.name = 'Terrain';
    terrain.visible = showTerrain;
    const occlusion = new THREE.Group();
    occlusion.name = 'Occlusion octants';
    occlusion.visible = showOcclusionOctants;
    const sky = new THREE.Group();
    sky.name = 'Sky';
    const currentProjection = new SceneProjection();
    currentProjection.setSelectionColor(selectionColor);
    const collisionScenes = new Set<THREE.Object3D>();
    const instancedCollisionPreview = new InstancedCollisionOverlayProjection(
      collisionScenes,
      () => setNotice('Collision candidate overlay unavailable.'),
    );
    content.add(terrain, occlusion, currentProjection.root, instancedCollisionPreview.root);
    skyScene.add(sky);
    scene.add(content);

    const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 80_000);

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.autoClear = false;
    renderer.info.autoReset = false;
    renderer.setPixelRatio(1);
    element.append(renderer.domElement);

    const composer = new EffectComposer(renderer);
    const skyPass = new RenderPass(skyScene, camera);
    const scenePass = new RenderPass(scene, camera);
    scenePass.clear = false;
    scenePass.clearDepth = true;
    const outlinePass = new OutlinePass(new THREE.Vector2(1, 1), scene, camera);
    setOutlineColor(outlinePass, selectionColor);
    outlinePass.edgeStrength = 4;
    outlinePass.edgeThickness = 3;
    outlinePass.downSampleRatio = 1;
    const toolPass = new RenderPass(toolScene, camera);
    toolPass.clear = false;
    toolPass.clearDepth = true;
    const outputPass = new OutputPass();
    composer.addPass(skyPass);
    composer.addPass(scenePass);
    composer.addPass(outlinePass);
    composer.addPass(toolPass);
    composer.addPass(outputPass);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.enableRotate = false;
    controls.zoomSpeed = 0.35;
    const movement = new Set<string>();
    const snapModifiers = new Set<string>();
    const velocity = new THREE.Vector3();
    const clock = new THREE.Clock();
    let cameraInputActive = false;
    let cancelPaintStroke = () => false;
    camera.position.set(0, 150, 300);
    controls.update();
    const snapRaycaster = new THREE.Raycaster();
    const snapPointer = new THREE.Vector2();
    const transformTool = new TransformTool(camera, renderer.domElement, toolScene, {
      preview: (preview) => currentProjection.sync(
        preview ?? currentEntities.current,
        currentSelection.current,
      ),
      commit: (updates) => {
        void transformsCommitted.current(updates).then((committed) => {
          if (committed) return;
          requestAnimationFrame(() => {
            if (viewport.current?.projection !== currentProjection) return;
            currentProjection.sync(
              currentEntities.current,
              currentSelection.current,
            );
            transformTool.sync(currentEntities.current, currentSelection.current, currentProjection);
          });
        });
      },
      commitSplinePoints: (entityId, points) => {
        void splinePointsCommitted.current(entityId, points).then((committed) => {
          if (committed) return;
          requestAnimationFrame(() => {
            if (viewport.current?.projection !== currentProjection) return;
            currentProjection.sync(currentEntities.current, currentSelection.current);
            transformTool.sync(currentEntities.current, currentSelection.current, currentProjection);
          });
        });
      },
      collectSnapVertices: (entityIds) => currentProjection.getWorldVertices(entityIds),
      resolveSnapTarget: (target, entityIds) => {
        snapRaycaster.setFromCamera(snapPointer, camera);
        return resolvePointerSnapTarget(
          target,
          snapRaycaster,
          currentProjection,
          [terrain, currentProjection.root],
          new Set(entityIds),
        );
      },
    });
    const dropGhost = new THREE.Mesh(
      new THREE.SphereGeometry(2, 16, 8),
      new THREE.MeshBasicMaterial({ color: 0x57b8ff, depthTest: false, transparent: true, opacity: 0.8 }),
    );
    dropGhost.name = 'Asset placement preview';
    dropGhost.visible = false;
    toolScene.add(dropGhost);
    const toggleCameraDuringTransform = (event: { value: unknown }) => {
      controls.enabled = event.value !== true;
    };
    transformTool.controls.addEventListener('dragging-changed', toggleCameraDuringTransform);
    viewport.current = {
      projection: currentProjection,
      scene,
      skyScene,
      toolScene,
      camera,
      controls,
      transformTool,
      outlinePass,
      content,
      terrain,
      occlusion,
      sky,
      skyEye: new THREE.Vector3(),
      updateSky: (deltaSeconds) => {
        viewport.current?.skyShells.forEach((shell) => shell.update(deltaSeconds));
      },
      velocity,
      framed: false,
      dropGhost,
      placedTemplates: new Map(),
      skyShells: new Map(),
      previewRequests: new Set(),
      collisionScenes,
      instancedCollisionPreview,
    };

    let noticeTimer: ReturnType<typeof setTimeout> | undefined;
    const showNotice = (message: string) => {
      setNotice(message);
      if (noticeTimer) clearTimeout(noticeTimer);
      noticeTimer = setTimeout(() => setNotice(undefined), 2_000);
    };
    const viewportInputActive = () => cameraInputActive
      || document.activeElement === renderer.domElement
      || transformTool.isInteracting;

    const keyDown = (event: KeyboardEvent) => {
      if (isTextInput(event.target)) return;
      if (SNAP_MODIFIER_KEYS.has(event.code)) {
        if (viewportInputActive()) {
          snapModifiers.add(event.code);
          transformTool.setSnapInverted(true);
        }
        return;
      }
      if (event.code === 'Escape' && transformTool.cancel()) {
        event.preventDefault();
        return;
      }
      if (event.code === 'Escape' && cancelPaintStroke()) {
        event.preventDefault();
        return;
      }
      const command = viewportInputActive()
        ? findKeybindingCommand(currentKeybindings.current, event, 'viewport')
        : undefined;
      const nextMode = transformModeForKeybinding(command);
      if (nextMode) {
        event.preventDefault();
        setMode(nextMode);
        return;
      }
      if (command === 'scene.snapToGround' && !transformTool.isInteracting) {
        event.preventDefault();
        if (event.repeat || !transformTool.controls.enabled) return;
        let result;
        try {
          result = buildGroundPlacement(
            currentEntities.current,
            currentSelection.current,
            currentProjection,
            [terrain, currentProjection.root],
          );
        } catch (cause) {
          showNotice(`Could not place selection: ${errorMessage(cause)}`);
          return;
        }
        if (!result.updates.length) showNotice(result.message);
        else void transformsCommitted.current(result.updates).then((committed) => {
          if (committed) showNotice(result.message);
        });
        return;
      }
      if (!viewportInputActive() || !MOVEMENT_KEYS.has(event.code)) return;
      if (transformTool.isInteracting) return;
      if (viewport.current) viewport.current.flight = undefined;
      movement.add(event.code);
      event.preventDefault();
    };
    const keyUp = (event: KeyboardEvent) => {
      movement.delete(event.code);
      if (!SNAP_MODIFIER_KEYS.has(event.code)) return;
      snapModifiers.delete(event.code);
      transformTool.setSnapInverted(snapModifiers.size > 0);
    };
    const clearSnapModifiers = () => {
      snapModifiers.clear();
      transformTool.setSnapInverted(false);
    };
    const deactivateCameraInput = () => {
      cameraInputActive = false;
      movement.clear();
      currentProjection.setHoveredEntityId(undefined);
      if (!transformTool.isInteracting && document.activeElement !== renderer.domElement) clearSnapModifiers();
    };
    const deactivateWindowInput = () => {
      cameraInputActive = false;
      movement.clear();
      clearSnapModifiers();
    };
    const activateCameraInput = () => { cameraInputActive = true; };
    window.addEventListener('keydown', keyDown, true);
    window.addEventListener('keyup', keyUp, true);
    window.addEventListener('blur', deactivateWindowInput);
    renderer.domElement.addEventListener('pointerenter', activateCameraInput);
    renderer.domElement.addEventListener('pointerleave', deactivateCameraInput);
    renderer.domElement.addEventListener('focus', activateCameraInput);
    renderer.domElement.addEventListener('blur', deactivateCameraInput);
    renderer.domElement.tabIndex = 0;

    const raycaster = new THREE.Raycaster();
    raycaster.params.Line.threshold = 8;
    const pointer = new THREE.Vector2();
    let pointerStart: { x: number; y: number } | undefined;
    let lookPointerId: number | undefined;
    let lookPosition: { x: number; y: number } | undefined;
    let selectOnLookRelease = false;
    let paintStroke: {
      pointerId: number;
      last: { x: number; y: number };
      faceIds: Set<number>;
      rawType: number;
    } | undefined;
    const paintAt = (clientX: number, clientY: number) => {
      const overlay = currentInstancedCollisionOverlay.current;
      if (!overlay?.paint?.active) return undefined;
      const bounds = renderer.domElement.getBoundingClientRect();
      pointer.set(
        (clientX - bounds.left) / bounds.width * 2 - 1,
        -(clientY - bounds.top) / bounds.height * 2 + 1,
      );
      raycaster.setFromCamera(pointer, camera);
      const hit = instancedCollisionPreview.pick(raycaster);
      instancedCollisionPreview.setHoveredFace(hit?.faceId, currentSelectionColor.current);
      overlay.paint.onHover(hit?.faceId, hit?.rawType);
      return hit;
    };
    cancelPaintStroke = () => {
      if (!paintStroke) return false;
      if (renderer.domElement.hasPointerCapture(paintStroke.pointerId))
        renderer.domElement.releasePointerCapture(paintStroke.pointerId);
      paintStroke = undefined;
      instancedCollisionPreview.finishStroke(false);
      return true;
    };
    const updateSnapPointer = (event: PointerEvent) => {
      const bounds = renderer.domElement.getBoundingClientRect();
      snapPointer.set(
        (event.clientX - bounds.left) / bounds.width * 2 - 1,
        -(event.clientY - bounds.top) / bounds.height * 2 + 1,
      );
    };
    const pointerDown = (event: PointerEvent) => {
      renderer.domElement.focus({ preventScroll: true });
      if (transformTool.isInteracting) return;
      currentProjection.setHoveredEntityId(undefined);
      if (viewport.current) viewport.current.flight = undefined;
      const paintState = currentInstancedCollisionOverlay.current?.paint;
      const paint = paintState?.active ? paintState : undefined;
      if (event.button === 0 && paint && !event.altKey) {
        const hit = paintAt(event.clientX, event.clientY);
        if (!hit) return;
        if (paint.interaction === 'eyedropper') {
          paint.onEyedropper(hit.rawType);
          return;
        }
        const rawType = paint.interaction === 'reset' ? paint.defaultRawType : paint.brushRawType;
        paintStroke = {
          pointerId: event.pointerId,
          last: { x: event.clientX, y: event.clientY },
          faceIds: new Set([hit.faceId]),
          rawType,
        };
        instancedCollisionPreview.previewFace(hit.faceId, rawType);
        paint.onHover(hit.faceId, rawType);
        renderer.domElement.setPointerCapture(event.pointerId);
        event.preventDefault();
        return;
      }
      if (paint
        ? shouldOrbitWhileCollisionPainting(event.button, event.altKey)
        : event.button === 0) {
        pointerStart = { x: event.clientX, y: event.clientY };
        lookPosition = pointerStart;
        lookPointerId = event.pointerId;
        selectOnLookRelease = !paint && event.button === 0;
        renderer.domElement.setPointerCapture(event.pointerId);
        if (event.button === 1) event.preventDefault();
      }
    };
    const pointerMove = (event: PointerEvent) => {
      if (transformTool.isInteracting) return;
      if (paintStroke && event.pointerId === paintStroke.pointerId) {
        for (const point of interpolatePointerSegment(
          paintStroke.last,
          { x: event.clientX, y: event.clientY },
        )) {
          const hit = paintAt(point.x, point.y);
          if (hit && !paintStroke.faceIds.has(hit.faceId)) {
            paintStroke.faceIds.add(hit.faceId);
            instancedCollisionPreview.previewFace(hit.faceId, paintStroke.rawType);
            currentInstancedCollisionOverlay.current?.paint?.onHover(hit.faceId, paintStroke.rawType);
          }
        }
        paintStroke.last = { x: event.clientX, y: event.clientY };
        event.preventDefault();
        return;
      }
      if (currentInstancedCollisionOverlay.current?.paint?.active
        && lookPointerId === undefined && !event.altKey) {
        paintAt(event.clientX, event.clientY);
        return;
      }
      if (lookPointerId === undefined) {
        const bounds = renderer.domElement.getBoundingClientRect();
        pointer.set(
          (event.clientX - bounds.left) / bounds.width * 2 - 1,
          -(event.clientY - bounds.top) / bounds.height * 2 + 1,
        );
        raycaster.setFromCamera(pointer, camera);
        currentProjection.setHoveredEntityId(
          currentProjection.resolvePick(raycaster.intersectObject(currentProjection.root, true)),
        );
        return;
      }
      if (event.pointerId !== lookPointerId || !lookPosition) return;
      rotateCamera(camera, controls.target, event.clientX - lookPosition.x, event.clientY - lookPosition.y);
      lookPosition = { x: event.clientX, y: event.clientY };
    };
    const pointerUp = (event: PointerEvent) => {
      if (transformTool.isInteracting) return;
      if (paintStroke && event.pointerId === paintStroke.pointerId) {
        const stroke = paintStroke;
        paintStroke = undefined;
        if (renderer.domElement.hasPointerCapture(event.pointerId)) renderer.domElement.releasePointerCapture(event.pointerId);
        const paintState = currentInstancedCollisionOverlay.current?.paint;
        const paint = paintState?.active ? paintState : undefined;
        if (!paint) instancedCollisionPreview.finishStroke(false);
        else void paint.onStroke([...stroke.faceIds], stroke.rawType)
          .then((committed) => instancedCollisionPreview.finishStroke(committed));
        event.preventDefault();
        return;
      }
      if (event.pointerId !== lookPointerId || !pointerStart) return;
      lookPointerId = undefined;
      lookPosition = undefined;
      if (renderer.domElement.hasPointerCapture(event.pointerId)) renderer.domElement.releasePointerCapture(event.pointerId);
      const distance = Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y);
      pointerStart = undefined;
      const select = selectOnLookRelease;
      selectOnLookRelease = false;
      if (!select || distance > 4) return;
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
      if (transformTool.isInteracting) return;
      if (paintStroke?.pointerId === event.pointerId) {
        cancelPaintStroke();
        return;
      }
      if (event.pointerId !== lookPointerId) return;
      pointerStart = undefined;
      lookPointerId = undefined;
      lookPosition = undefined;
      selectOnLookRelease = false;
      if (renderer.domElement.hasPointerCapture(event.pointerId)) renderer.domElement.releasePointerCapture(event.pointerId);
    };
    renderer.domElement.addEventListener('pointerdown', updateSnapPointer, true);
    renderer.domElement.addEventListener('pointermove', updateSnapPointer, true);
    renderer.domElement.addEventListener('pointerdown', pointerDown);
    renderer.domElement.addEventListener('pointermove', pointerMove);
    renderer.domElement.addEventListener('pointerup', pointerUp);
    renderer.domElement.addEventListener('pointercancel', pointerCancel);
    renderer.domElement.addEventListener('contextmenu', preventDefault);
    renderer.domElement.addEventListener('auxclick', preventDefault);

    const resize = () => {
      const clientWidth = Math.max(element.clientWidth, 1);
      const clientHeight = Math.max(element.clientHeight, 1);
      camera.aspect = clientWidth / clientHeight;
      camera.updateProjectionMatrix();
      renderer.setSize(clientWidth, clientHeight, false);
      composer.setSize(clientWidth, clientHeight);
      currentProjection.setViewportSize(clientWidth, clientHeight);
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
      viewport.current?.updateSky(delta);
      sky.position.copy(camera.position).sub(viewport.current?.skyEye ?? ZERO_VECTOR);
      controls.update();
      transformTool.update();
      currentProjection.updateBillboards(camera, element.clientHeight);
      renderer.info.reset();
      outlinePass.selectedObjects = currentProjection.getSelectionOutlineObjects();
      composer.render(delta);
      const now = performance.now();
      if (currentShowStats.current) {
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
      if (noticeTimer) clearTimeout(noticeTimer);
      if (dropFrame.current !== undefined) cancelAnimationFrame(dropFrame.current);
      dropFrame.current = undefined;
      pendingDrop.current = undefined;
      renderer.domElement.removeEventListener('pointerdown', updateSnapPointer, true);
      renderer.domElement.removeEventListener('pointermove', updateSnapPointer, true);
      renderer.domElement.removeEventListener('pointerdown', pointerDown);
      renderer.domElement.removeEventListener('pointermove', pointerMove);
      renderer.domElement.removeEventListener('pointerup', pointerUp);
      renderer.domElement.removeEventListener('pointercancel', pointerCancel);
      renderer.domElement.removeEventListener('contextmenu', preventDefault);
      renderer.domElement.removeEventListener('auxclick', preventDefault);
      window.removeEventListener('keydown', keyDown, true);
      window.removeEventListener('keyup', keyUp, true);
      window.removeEventListener('blur', deactivateWindowInput);
      renderer.domElement.removeEventListener('pointerenter', activateCameraInput);
      renderer.domElement.removeEventListener('pointerleave', deactivateCameraInput);
      renderer.domElement.removeEventListener('focus', activateCameraInput);
      renderer.domElement.removeEventListener('blur', deactivateCameraInput);
      renderer.setAnimationLoop(null);
      transformTool.controls.removeEventListener('dragging-changed', toggleCameraDuringTransform);
      transformTool.dispose();
      dropGhost.geometry.dispose();
      (dropGhost.material as THREE.Material).dispose();
      controls.dispose();
      sky.removeFromParent();
      sky.clear();
      terrain.removeFromParent();
      terrain.clear();
      currentProjection.dispose();
      instancedCollisionPreview.dispose();
      viewport.current?.previewRequests.forEach((token) => { void window.forge.cancelAssetPreview(token); });
      viewport.current?.placedTemplates.forEach(disposeObject);
      viewport.current?.skyShells.forEach((shell) => disposeObject(shell.root));
      if (viewport.current?.projection === currentProjection) viewport.current = null;
      outlinePass.dispose();
      outputPass.dispose();
      composer.dispose();
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
    };
  }, []);

  const instancedCollisionEntity = instancedCollisionOverlay
    ? entities.find((entity) => entity.id === instancedCollisionOverlay.entityId)
    : undefined;
  const instancedCollisionTransform = instancedCollisionEntity ? JSON.stringify(instancedCollisionEntity.transform) : '';

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    current.projection.setPreviewHiddenEntity(
      instancedCollisionOverlay && !instancedCollisionOverlay.showSource ? instancedCollisionOverlay.entityId : undefined);
    current.projection.sync(
      currentEntities.current,
      currentSelection.current,
      undefined,
      true,
      currentCollisionVisibility.current,
    );
    current.transformTool.sync(currentEntities.current, currentSelection.current, current.projection);
  }, [instancedCollisionOverlay?.entityId, instancedCollisionOverlay?.showSource]);

  useEffect(() => {
    const current = viewport.current;
    const overlay = instancedCollisionOverlay;
    if (!current || !overlay || !instancedCollisionEntity) return;
    current.instancedCollisionPreview.show(
      overlay,
      instancedCollisionEntity.transform,
      currentCollisionVisualization.current,
    );
    return () => current.instancedCollisionPreview.clear();
  }, [instancedCollisionOverlay?.candidate?.token, instancedCollisionOverlay?.url, instancedCollisionTransform]);

  useEffect(() => {
    const projection = viewport.current?.instancedCollisionPreview;
    const overlay = instancedCollisionOverlay;
    if (!projection || !overlay) return;
    projection.update(overlay);
  }, [instancedCollisionOverlay?.paint, instancedCollisionOverlay?.showOctants,
    instancedCollisionOverlay?.showProxy, instancedCollisionOverlay?.wireframe]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    (instancedCollisionOverlay?.paint?.active ? current.toolScene : current.content)
      .add(current.instancedCollisionPreview.root);
  }, [Boolean(instancedCollisionOverlay?.paint?.active)]);

  useEffect(() => {
    if (!terrainSource) return;
    let disposed = false;
    const loadedScenes: THREE.Object3D[] = [];
    const octants = createOcclusionOverlay(terrainSource.occlusionOctants, sceneTreeColors.occlusionOctant);
    if (octants) {
      viewport.current!.occlusion.add(octants);
      loadedScenes.push(octants);
    }
    const total = terrainSource.urls.length + terrainSource.assets.length;
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
    applySceneEnvironment(viewport.current!.scene, currentEnvironment.current, viewport.current!.skyScene);
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
        configurePs2MaterialAlpha(scene, 'tfrag');
        configurePs2MaterialFog(scene, currentEnvironment.current);
        scene.traverse((object) => { if (/^lod_[1-9]/i.test(object.name)) object.visible = false; });
        viewport.current.terrain.add(scene);
        loadedScenes.push(scene);
      }
      const templates = new Map<string, THREE.Object3D>();
      const failedAssets = new Set<string>();
      const collisionEntitiesByAsset = new Map<string, EditorEntity[]>();
      for (const entity of currentEntities.current) {
        if (!entity.collision || !entity.asset) continue;
        const matches = collisionEntitiesByAsset.get(entity.asset.id);
        if (matches) matches.push(entity);
        else collisionEntitiesByAsset.set(entity.asset.id, [entity]);
      }
      assetResults.forEach((result, index) => {
        const asset = terrainSource.assets[index];
        if (result.status === 'fulfilled') {
          if (asset.kind !== 'collision') configurePs2MaterialAlpha(result.value.scene, asset.kind);
          configurePs2MaterialFog(result.value.scene, currentEnvironment.current);
          if (asset.kind === 'collision') {
            configureCollisionMaterials(result.value.scene, currentCollisionVisualization.current);
            viewport.current!.collisionScenes.add(result.value.scene);
            let found = false;
            const nodes = new Map<string, THREE.Object3D>();
            result.value.scene.traverse((node) => { if (node.name) nodes.set(node.name, node); });
            collisionEntitiesByAsset.get(asset.assetId)?.forEach((entity) => {
              const prefix = entity.collision!.kind === 'solid' ? 'solid_collision' : 'player_barrier';
              const node = nodes.get(
                `${prefix}_${String(entity.collision!.sourcePieceIndex).padStart(4, '0')}`,
              );
              const key = assetTemplateKey(entity);
              if (node && key) {
                templates.set(key, node);
                found = true;
              }
            });
            if (!found) failedAssets.add(asset.assetId);
          } else templates.set(asset.assetId, result.value.scene);
          loadedScenes.push(result.value.scene);
        } else {
          failedAssets.add(asset.assetId);
        }
      });
      viewport.current.projection.setAssetTemplates(templates, failedAssets);
      viewport.current.projection.sync(
        currentEntities.current,
        currentSelection.current,
        undefined,
        true,
        currentCollisionVisibility.current,
      );
      viewport.current.transformTool.sync(currentEntities.current, currentSelection.current, viewport.current.projection);
      if (!viewport.current.framed) {
        if (!positionCameraAtPreferredMoby(
          viewport.current.camera, viewport.current.controls, currentEntities.current,
        ) && !frameEntities(viewport.current.camera, viewport.current.controls, currentEntities.current))
          frameObject(viewport.current.camera, viewport.current.controls, viewport.current.content);
        viewport.current.framed = true;
      }
      const terrainFailures = terrainResults.length - loaded.length;
      const firstAssetFailure = assetResults.findIndex((result) => result.status === 'rejected');
      const firstAssetError = firstAssetFailure < 0 ? '' : terrainSource.assets[firstAssetFailure].error
        ?? String((assetResults[firstAssetFailure] as PromiseRejectedResult).reason);
      const failures = [
        terrainFailures && `${terrainFailures} terrain section${terrainFailures === 1 ? '' : 's'}`,
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
      octants?.removeFromParent();
      if (viewport.current) {
        viewport.current.skyEye.set(0, 0, 0);
        applySceneEnvironment(viewport.current.scene, undefined, viewport.current.skyScene);
      }
      for (const scene of loadedScenes) disposeObject(scene);
      if (viewport.current) loadedScenes.forEach((scene) => viewport.current!.collisionScenes.delete(scene));
    };
  }, [terrainSource]);

  useEffect(() => {
    viewport.current?.collisionScenes.forEach((scene) => configureCollisionMaterials(scene, collisionVisualization));
  }, [collisionVisualization]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    applySceneEnvironment(current.scene, environment, current.skyScene);
    configurePs2MaterialFog(current.content, environment);
  }, [environment]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    let disposed = false;
    const desired = new Map(currentEntities.current.filter((entity) => entity.skyShell && entity.asset)
      .map((entity) => [entity.id, entity]));
    current.skyShells.forEach((shell, entityId) => {
      if (desired.has(entityId)) return;
      current.skyShells.delete(entityId);
      disposeObject(shell.root);
    });
    syncSkyShellProjections(current, currentEntities.current);

    const requests = new Set<string>();
    for (const entity of desired.values()) {
      if (current.skyShells.has(entity.id)) continue;
      const requestToken = crypto.randomUUID();
      requests.add(requestToken);
      current.previewRequests.add(requestToken);
      void window.forge.getAssetPreview(
        entity.asset!.id, 'sky', requestToken, entity.skyShell!.sourceShellIndex,
      ).then((source) => new GLTFLoader().loadAsync(source.url))
        .then((gltf) => {
          if (disposed || viewport.current !== current || !desired.has(entity.id)
            || current.skyShells.has(entity.id)) {
            disposeObject(gltf.scene);
            return;
          }
          configurePs2MaterialAlpha(gltf.scene, 'sky');
          const configured = configureSkybox(gltf.scene);
          current.skyShells.set(entity.id, {
            root: gltf.scene,
            bounds: configured.bounds,
            update: configured.update,
            setRotation: configured.setRotation,
          });
          current.sky.add(gltf.scene);
          syncSkyShellProjections(current, currentEntities.current);
        })
        .catch(() => {
          if (!disposed && viewport.current === current && desired.has(entity.id))
            setNotice(`Sky shell ${entity.skyShell!.order + 1} preview unavailable.`);
        })
        .finally(() => {
          requests.delete(requestToken);
          current.previewRequests.delete(requestToken);
        });
    }
    return () => {
      disposed = true;
      requests.forEach((token) => {
        current.previewRequests.delete(token);
        void window.forge.cancelAssetPreview(token);
      });
    };
  }, [skySourceSignature]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    current.projection.setSelectionColor(selectionColor);
    setOutlineColor(current.outlinePass, selectionColor);
    current.projection.sync(
      entities,
      selection,
      undefined,
      true,
      currentCollisionVisibility.current,
    );
    syncSkyShellProjections(current, entities);
    current.transformTool.sync(entities, selection, current.projection);
    if (!current.framed && entities.length) {
      if (!positionCameraAtPreferredMoby(current.camera, current.controls, entities))
        frameEntities(current.camera, current.controls, entities);
      current.framed = true;
    }
  }, [entities, selection, selectionColor, showPlayerBarriers, showSolidCollision]);

  useEffect(() => {
    const next = selection.filter((id) => {
      const collision = entities.find((entity) => entity.id === id)?.collision;
      return !collision || currentCollisionVisibility.current.has(collision.kind);
    });
    if (next.length !== selection.length) selectionChanged.current(next);
  }, [entities, selection, showPlayerBarriers, showSolidCollision]);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    current.projection.setGeometryColors(sceneTreeColors);
    current.occlusion.traverse((object) => {
      if (object instanceof THREE.Mesh && object.material instanceof THREE.MeshBasicMaterial)
        object.material.color.set(sceneTreeColors.occlusionOctant);
      if (object instanceof THREE.LineSegments && object.material instanceof THREE.LineBasicMaterial)
        object.material.color.set(sceneTreeColors.occlusionOctant);
    });
  }, [sceneTreeColors]);
  useEffect(() => { if (viewport.current) viewport.current.occlusion.visible = showOcclusionOctants; }, [showOcclusionOctants]);
  useEffect(() => { if (viewport.current) viewport.current.terrain.visible = showTerrain; }, [showTerrain]);

  useEffect(() => { if (!showStats) setStats(undefined); }, [showStats]);

  useEffect(() => viewport.current?.transformTool.setMode(mode), [mode]);
  useEffect(() => viewport.current?.transformTool.setSpace(space), [space]);
  useEffect(() => viewport.current?.transformTool.setSnapping(
    snapEnabled, translationSnap, rotationSnap, scaleSnap, snapSource, snapTarget,
  ), [rotationSnap, scaleSnap, snapEnabled, snapSource, snapTarget, translationSnap]);
  useEffect(() => viewport.current?.transformTool.setEnabled(!disabled && !instancedCollisionOverlay?.paint?.active),
    [disabled, instancedCollisionOverlay?.paint?.active]);
  useEffect(() => {
    const canvas = container.current?.querySelector('canvas');
    if (canvas) canvas.style.cursor = instancedCollisionOverlay?.paint?.active ? 'crosshair' : '';
  }, [instancedCollisionOverlay?.paint?.active]);

  useEffect(() => {
    if (!focusEntityId) return;
    const current = viewport.current;
    const entity = currentEntities.current.find((value) => value.id === focusEntityId);
    focusHandled.current();
    if (!current || !entity || entity.skyShell) return;
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

  const positionAssetDrop = (clientX: number, clientY: number) => {
    const current = viewport.current;
    const bounds = container.current?.getBoundingClientRect();
    if (!current || !bounds) return;
    DROP_POINTER.set(
      (clientX - bounds.left) / bounds.width * 2 - 1,
      -(clientY - bounds.top) / bounds.height * 2 + 1,
    );
    DROP_RAYCASTER.setFromCamera(DROP_POINTER, current.camera);
    const targets = [current.terrain, current.projection.root];
    const cursorPoint = DROP_RAYCASTER.intersectObjects(targets, true)
      .find((intersection) => current.projection.isPlacementSurface(intersection))?.point
      ?? DROP_RAYCASTER.ray.intersectPlane(DROP_PLANE, DROP_POINT);
    const point = cursorPoint && createGroundSurfaceRaycast(
      currentEntities.current, current.projection, targets,
    )(cursorPoint.x, cursorPoint.z, cursorPoint.y + 0.01);
    current.dropGhost.visible = Boolean(point);
    if (point) current.dropGhost.position.copy(point);
  };

  const updateAssetDrop = (event: DragEvent<HTMLDivElement>) => {
    if (!event.dataTransfer.types.includes(ASSET_PLACEMENT_MIME)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    if (event.dataTransfer.types.includes(SKY_SHELL_PLACEMENT_MIME)) {
      setSkyDropActive(true);
      pendingDrop.current = undefined;
      if (dropFrame.current !== undefined) cancelAnimationFrame(dropFrame.current);
      dropFrame.current = undefined;
      if (viewport.current) viewport.current.dropGhost.visible = false;
      return;
    }
    setSkyDropActive(false);
    pendingDrop.current = { x: event.clientX, y: event.clientY };
    if (dropFrame.current !== undefined) return;
    dropFrame.current = requestAnimationFrame(() => {
      dropFrame.current = undefined;
      const next = pendingDrop.current;
      pendingDrop.current = undefined;
      if (next) positionAssetDrop(next.x, next.y);
    });
  };

  const preparePlacedAsset = async (asset: AssetModelPlacementDragData): Promise<number | undefined> => {
    const current = viewport.current;
    if (!current) return undefined;
    const groundOffset = () => {
      const minY = current.projection.getAssetTemplateBounds(asset.assetId)?.min.y;
      return minY !== undefined && Number.isFinite(minY) ? -minY : 0;
    };
    if (current.projection.hasAssetTemplate(asset.assetId)) return groundOffset();
    const requestToken = crypto.randomUUID();
    current.previewRequests.add(requestToken);
    let root: THREE.Object3D | undefined;
    try {
      const source = await window.forge.getAssetPreview(asset.assetId, asset.kind, requestToken);
      root = (await new GLTFLoader().loadAsync(source.url)).scene;
      if (viewport.current !== current) return undefined;
      if (!configurePs2AssetVisibility(root, asset.kind)) throw new Error('Asset has no renderable mesh data.');
      configurePs2MaterialAlpha(root, asset.kind);
      configurePs2MaterialFog(root, currentEnvironment.current);
      if (!current.projection.hasAssetTemplate(asset.assetId)) {
        current.placedTemplates.set(asset.assetId, root);
        current.projection.addAssetTemplate(asset.assetId, root);
        root = undefined;
      }
      return groundOffset();
    } catch {
      if (viewport.current === current) setNotice('Asset preview unavailable; placing from its origin.');
      return viewport.current === current ? 0 : undefined;
    } finally {
      current.previewRequests.delete(requestToken);
      if (root) disposeObject(root);
    }
  };

  const dropAsset = (event: DragEvent<HTMLDivElement>) => {
    const asset = readAssetPlacementDrag(event.dataTransfer);
    const current = viewport.current;
    if (!asset || !current) return;
    event.preventDefault();
    if (dropFrame.current !== undefined) cancelAnimationFrame(dropFrame.current);
    dropFrame.current = undefined;
    pendingDrop.current = undefined;
    setSkyDropActive(false);
    if (asset.kind === 'sky') {
      current.dropGhost.visible = false;
      void assetDropped.current(asset).then((placed) => {
        setNotice(placed ? 'Sky shell added.' : 'Sky shell addition failed.');
      });
      return;
    }
    positionAssetDrop(event.clientX, event.clientY);
    if (!current.dropGhost.visible) return;
    const point = current.dropGhost.position.clone();
    current.dropGhost.visible = false;
    void preparePlacedAsset(asset).then(async (offset) => {
      if (offset === undefined) return;
      const placed = await assetDropped.current(asset, { x: point.x, y: -point.z, z: point.y + offset });
      if (!placed) setNotice('Asset placement failed.');
    });
  };

  return (
    <div
      aria-label="3D scene viewport"
      className="scene-viewport"
      onDragOver={updateAssetDrop}
      onDragLeave={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null) && viewport.current) {
          if (dropFrame.current !== undefined) cancelAnimationFrame(dropFrame.current);
          dropFrame.current = undefined;
          pendingDrop.current = undefined;
          viewport.current.dropGhost.visible = false;
          setSkyDropActive(false);
        }
      }}
      onDrop={dropAsset}
    >
      <div className="scene-canvas" ref={container} />
      {skyDropActive && <div className="scene-drop-affordance" role="status">Add sky shell</div>}
      <ViewportToolbar
        mode={mode}
        space={space}
        snapSource={snapSource}
        snapTarget={snapTarget}
        snapEnabled={snapEnabled}
        translationSnap={translationSnap}
        rotationSnap={rotationSnap}
        scaleSnap={scaleSnap}
        showSolidCollision={showSolidCollision}
        showPlayerBarriers={showPlayerBarriers}
        onModeChange={setMode}
        onSpaceChange={setSpace}
        onSnapSourceChange={setSnapSource}
        onSnapTargetChange={setSnapTarget}
        onSnapEnabledChange={setSnapEnabled}
        onTranslationSnapChange={setTranslationSnap}
        onRotationSnapChange={setRotationSnap}
        onScaleSnapChange={setScaleSnap}
        onSolidCollisionVisibilityChange={onSolidCollisionVisibilityChange}
        onPlayerBarrierVisibilityChange={onPlayerBarrierVisibilityChange}
      />
      {notice && <div className="scene-notice" role="status">{notice}</div>}
      {showStats && <div className="scene-stats">
        {stats ? `${stats.fps} FPS · ${stats.calls} calls · ${stats.triangles.toLocaleString()} tris` : 'Measuring…'}
      </div>}
    </div>
  );
}

function syncSkyShellProjections(
  current: { skyShells: Map<string, SkyShellProjection>; skyEye: THREE.Vector3 },
  entities: readonly EditorEntity[],
): void {
  const shells = entities.filter((entity) => entity.skyShell)
    .sort((left, right) => left.skyShell!.order - right.skyShell!.order);
  const bounds = new THREE.Box3();
  for (const entity of shells) {
    const projection = current.skyShells.get(entity.id);
    if (!projection) continue;
    projection.root.visible = !entity.state.hidden && !entity.state.disabled;
    if (projection.root.visible) bounds.union(projection.bounds);
    projection.setRotation(
      entity.skyShell!.initialRotationRadians,
      entity.skyShell!.angularVelocityRadiansPerSecond,
    );
    projection.root.traverse((object) => {
      const base = typeof object.userData.forgeSkyRenderOrder === 'number'
        ? object.userData.forgeSkyRenderOrder : object.renderOrder;
      object.userData.forgeSkyRenderOrder = base;
      object.renderOrder = base + entity.skyShell!.order * 100;
    });
  }
  skyEyeFromBounds(bounds, current.skyEye);
}

function createOcclusionOverlay(
  octants: readonly { x: number; y: number; z: number; maskIndex: number }[],
  color: string,
): THREE.Group | undefined {
  if (!octants.length) return undefined;
  const geometry = new THREE.BoxGeometry(4, 4, 4);
  const fill = new THREE.InstancedMesh(geometry, new THREE.MeshBasicMaterial({
    color, depthWrite: false, fog: false, opacity: 0.08, transparent: true,
  }), octants.length);
  const edgeGeometry = new THREE.EdgesGeometry(geometry);
  const edgeVertices = edgeGeometry.getAttribute('position');
  const edgePositions = new Float32Array(octants.length * edgeVertices.count * 3);
  const matrix = new THREE.Matrix4();
  octants.forEach((octant, index) => {
    const x = octant.x * 4 + 2;
    const y = octant.z * 4 + 2;
    const z = -(octant.y * 4 + 2);
    matrix.makeTranslation(x, y, z);
    fill.setMatrixAt(index, matrix);
    for (let vertex = 0; vertex < edgeVertices.count; vertex += 1) {
      const offset = (index * edgeVertices.count + vertex) * 3;
      edgePositions[offset] = edgeVertices.getX(vertex) + x;
      edgePositions[offset + 1] = edgeVertices.getY(vertex) + y;
      edgePositions[offset + 2] = edgeVertices.getZ(vertex) + z;
    }
  });
  fill.instanceMatrix.needsUpdate = true;
  edgeGeometry.setAttribute('position', new THREE.BufferAttribute(edgePositions, 3));
  edgeGeometry.computeBoundingSphere();
  const edges = new THREE.LineSegments(edgeGeometry, new THREE.LineBasicMaterial({
    color, fog: false, opacity: 0.55, transparent: true,
  }));
  const overlay = new THREE.Group();
  overlay.name = 'Occlusion octants';
  overlay.add(fill, edges);
  return overlay;
}

function collisionVisibility(
  solid: boolean,
  playerBarriers: boolean,
): ReadonlySet<'solid' | 'playerBarrier'> {
  return new Set([
    ...(solid ? ['solid' as const] : []),
    ...(playerBarriers ? ['playerBarrier' as const] : []),
  ]);
}

function setOutlineColor(pass: OutlinePass, color: THREE.ColorRepresentation): void {
  const outline = new THREE.Color(color).multiplyScalar(0.7);
  pass.visibleEdgeColor.copy(outline);
  pass.hiddenEdgeColor.copy(outline);
}

const MOVEMENT_KEYS = new Set(['KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyQ', 'KeyE', 'Space', 'ShiftLeft', 'ShiftRight']);
const SNAP_MODIFIER_KEYS = new Set(['ControlLeft', 'ControlRight']);
const ZERO_VECTOR = new THREE.Vector3();
const DROP_POINTER = new THREE.Vector2();
const DROP_POINT = new THREE.Vector3();
const DROP_RAYCASTER = new THREE.Raycaster();
const DROP_PLANE = new THREE.Plane(new THREE.Vector3(0, 1, 0));

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
