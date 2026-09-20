import { useEffect, useRef } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import { frameObject } from '../../utils/Scene.ts';
import { SceneProjection } from './SceneProjection.ts';

interface SceneViewportProps {
  entities: readonly EditorEntity[];
}

export function SceneViewport({ entities }: SceneViewportProps) {
  const container = useRef<HTMLDivElement>(null);
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
    current.projection.sync(entities);
    if (!current.framed && entities.length) {
      frameObject(current.camera, current.controls, current.projection.root);
      current.framed = true;
    }
  }, [entities]);

  return (
    <div aria-label="3D scene viewport" className="scene-viewport">
      <div className="scene-canvas" ref={container} />
    </div>
  );
}
