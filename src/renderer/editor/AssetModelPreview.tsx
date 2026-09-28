import { ArrowCounterClockwiseIcon } from '@phosphor-icons/react/dist/csr/ArrowCounterClockwise';
import { ActionIcon, Loader, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

import type { AssetPreviewKind } from '../../types/AssetExplorer.js';
import { errorMessage } from '../../utils/Errors.ts';
import { configurePs2AssetPreview } from '../../utils/Ps2Materials.ts';
import { disposeObject, frameCameraOnObject } from '../../utils/Scene.ts';

interface AssetModelPreviewProps {
  assetId: string;
  kind: AssetPreviewKind;
  label: string;
}

export function AssetModelPreview({ assetId, kind, label }: AssetModelPreviewProps) {
  const container = useRef<HTMLDivElement>(null);
  const resetView = useRef<() => void>(() => undefined);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [error, setError] = useState('');

  useEffect(() => {
    const element = container.current;
    if (!element) return;
    let disposed = false;
    let root: THREE.Object3D | undefined;
    let renderer: THREE.WebGLRenderer;
    try {
      renderer = new THREE.WebGLRenderer({ antialias: true });
    } catch (cause) {
      setState('error');
      setError(errorMessage(cause));
      return;
    }
    setState('loading');
    setError('');
    renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    renderer.domElement.tabIndex = 0;
    renderer.domElement.setAttribute(
      'aria-label',
      `${label} interactive model preview. Use arrow keys to orbit, plus and minus to zoom, and Home to reset.`,
    );
    element.append(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x151922);
    scene.add(new THREE.HemisphereLight(0xffffff, 0x5c6370, 2));
    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 10_000);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.enablePan = false;
    controls.zoomSpeed = 0.5;
    const resize = () => {
      const width = Math.max(1, element.clientWidth);
      const height = Math.max(1, element.clientHeight);
      renderer.setSize(width, height, false);
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
    };
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(element);
    renderer.setAnimationLoop(() => {
      controls.update();
      renderer.render(scene, camera);
    });

    const frame = () => {
      if (!root) return;
      frameCameraOnObject(camera, root, controls.target);
      controls.update();
    };
    resetView.current = frame;
    const requestToken = crypto.randomUUID();
    const keyDown = (event: KeyboardEvent) => {
      const angle = Math.PI / 18;
      if (event.key === 'ArrowLeft') controls.rotateLeft(angle);
      else if (event.key === 'ArrowRight') controls.rotateLeft(-angle);
      else if (event.key === 'ArrowUp') controls.rotateUp(angle);
      else if (event.key === 'ArrowDown') controls.rotateUp(-angle);
      else if (event.key === '+' || event.key === '=') controls.dollyIn(0.9);
      else if (event.key === '-' || event.key === '_') controls.dollyOut(0.9);
      else if (event.key === 'Home') frame();
      else return;
      event.preventDefault();
    };
    renderer.domElement.addEventListener('keydown', keyDown);
    void window.forge.getAssetPreview(assetId, kind, requestToken).then((source) => {
      if (disposed) return;
      return new GLTFLoader().loadAsync(source.url);
    }).then((gltf) => {
      if (!gltf) return;
      root = gltf.scene;
      if (disposed) {
        disposeObject(root);
        root = undefined;
        return;
      }
      if (!configurePs2AssetPreview(root, kind)) throw new Error('Asset has no renderable mesh data.');
      scene.add(root);
      frame();
      setState('ready');
    }).catch((cause: unknown) => {
      if (!disposed) {
        setState('error');
        setError(errorMessage(cause));
      }
    });

    return () => {
      disposed = true;
      void window.forge.cancelAssetPreview(requestToken);
      resetView.current = () => undefined;
      observer.disconnect();
      renderer.setAnimationLoop(null);
      renderer.domElement.removeEventListener('keydown', keyDown);
      controls.dispose();
      if (root) disposeObject(root);
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
    };
  }, [assetId, kind, label]);

  return <div className="asset-model-preview">
    <div className="asset-model-preview-canvas" ref={container} />
    {state === 'loading' && <div className="asset-model-preview-state" role="status">
      <Loader size="sm" />
      <Text size="sm">Loading preview…</Text>
    </div>}
    {state === 'error' && <Text className="asset-model-preview-state" c="red" role="alert" size="sm">
      {error || 'Preview unavailable.'}
    </Text>}
    <ActionIcon
      aria-label="Reset model preview view"
      className="asset-model-preview-reset"
      disabled={state !== 'ready'}
      title="Reset view"
      variant="default"
      onClick={() => resetView.current()}
    >
      <ArrowCounterClockwiseIcon />
    </ActionIcon>
  </div>;
}
