import { useEffect, useRef } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import { frameObject } from '../../utils/Scene.ts';
import { nextViewportSelection } from './EditorPanelState.ts';
import { SceneProjection } from './SceneProjection.ts';

interface SceneViewportProps {
  entities: readonly EditorEntity[];
  selection: readonly string[];
  onSelectionChange(values: string[]): void;
}

export function SceneViewport({ entities, selection, onSelectionChange }: SceneViewportProps) {
  const container = useRef<HTMLDivElement>(null);
  const currentSelection = useRef(selection);
  const selectionChanged = useRef(onSelectionChange);
  currentSelection.current = selection;
  selectionChanged.current = onSelectionChange;
  const viewport = useRef<{
    projection: SceneProjection;
    camera: THREE.PerspectiveCamera;
    controls: OrbitControls;
    framed: boolean;
  }>(null);

  useEffect(() => {
    const element = container.current;
    if (!element) return;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x151922);
    const currentProjection = new SceneProjection();
    scene.add(currentProjection.root);

    const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 80_000);

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    element.append(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    camera.position.set(0, 150, 300);
    controls.update();
    viewport.current = { projection: currentProjection, camera, controls, framed: false };

    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    let pointerStart: { x: number; y: number } | undefined;
    const pointerDown = (event: PointerEvent) => {
      if (event.button === 0) pointerStart = { x: event.clientX, y: event.clientY };
    };
    const pointerUp = (event: PointerEvent) => {
      if (event.button !== 0 || !pointerStart) return;
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
    renderer.domElement.addEventListener('pointerdown', pointerDown);
    renderer.domElement.addEventListener('pointerup', pointerUp);

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
      controls.update();
      renderer.render(scene, camera);
    });

    return () => {
      observer.disconnect();
      renderer.domElement.removeEventListener('pointerdown', pointerDown);
      renderer.domElement.removeEventListener('pointerup', pointerUp);
      renderer.setAnimationLoop(null);
      controls.dispose();
      currentProjection.dispose();
      if (viewport.current?.projection === currentProjection) viewport.current = null;
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
    };
  }, []);

  useEffect(() => {
    const current = viewport.current;
    if (!current) return;
    current.projection.sync(entities, selection);
    if (!current.framed && entities.length) {
      frameObject(current.camera, current.controls, current.projection.root);
      current.framed = true;
    }
  }, [entities, selection]);

  return (
    <div aria-label="3D scene viewport" className="scene-viewport">
      <div className="scene-canvas" ref={container} />
    </div>
  );
}
