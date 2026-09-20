import { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

import { disposeObject, frameObject } from '../utils/Scene.ts';

export function SceneViewport() {
  const container = useRef<HTMLDivElement>(null);
  const [status, setStatus] = useState('Loading terrain…');

  useEffect(() => {
    const element = container.current;
    if (!element) return;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x151922);

    const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 80_000);

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    element.append(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    camera.position.set(0, 150, 300);
    controls.update();

    const resize = () => {
      const { clientWidth, clientHeight } = element;
      camera.aspect = clientWidth / Math.max(clientHeight, 1);
      camera.updateProjectionMatrix();
      renderer.setSize(clientWidth, clientHeight, false);
    };

    const observer = new ResizeObserver(resize);
    observer.observe(element);
    resize();

    let terrain: THREE.Object3D | undefined;
    let disposed = false;
    void window.forge.getTfragUrl().then(async (url) => {
      if (!url) throw new Error('Set FORGE_TFRAG_PATH to a terrain.gltf file');
      const gltf = await new GLTFLoader().loadAsync(url);
      if (disposed) return disposeObject(gltf.scene);

      terrain = gltf.scene;
      terrain.traverse((object) => {
        if (/^lod_[1-9]/i.test(object.name)) object.visible = false;
      });
      scene.add(terrain);
      frameObject(camera, controls, terrain);
      setStatus('');
    }).catch((error: unknown) => {
      if (!disposed) setStatus(error instanceof Error ? error.message : 'Could not load terrain');
    });

    renderer.setAnimationLoop(() => {
      controls.update();
      renderer.render(scene, camera);
    });

    return () => {
      disposed = true;
      observer.disconnect();
      renderer.setAnimationLoop(null);
      controls.dispose();
      if (terrain) disposeObject(terrain);
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
    };
  }, []);

  return (
    <div aria-label="3D scene viewport" className="scene-viewport">
      <div className="scene-canvas" ref={container} />
      {status && <div className="scene-status">{status}</div>}
    </div>
  );
}
