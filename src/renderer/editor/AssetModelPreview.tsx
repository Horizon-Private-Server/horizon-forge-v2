import { ArrowCounterClockwiseIcon } from '@phosphor-icons/react/dist/csr/ArrowCounterClockwise';
import { ActionIcon, Loader, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

import type { AssetModelKind, AssetPreviewSource } from '../../types/AssetExplorer.js';
import { errorMessage } from '../../utils/Errors.ts';
import { configurePs2AssetPreview } from '../../utils/Ps2Materials.ts';
import { disposeObject, frameCameraOnObject } from '../../utils/Scene.ts';
import { configureSkybox } from '../../utils/SkyboxScene.ts';

interface AssetModelPreviewProps {
  assetId: string;
  kind: AssetModelKind | 'tfrag' | 'sky';
  label: string;
  shellIndex?: number;
  customView?: 'source' | 'target';
  previewRevision?: string;
  onSource?: (source: AssetPreviewSource) => void;
}

export function AssetModelPreview({
  assetId, kind, label, shellIndex, customView, previewRevision, onSource,
}: AssetModelPreviewProps) {
  const container = useRef<HTMLDivElement>(null);
  const resetView = useRef<() => void>(() => undefined);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    const element = container.current;
    if (!element) return;
    const observer = new IntersectionObserver((entries) => {
      setVisible(entries.some((entry) => entry.isIntersecting));
    }, { rootMargin: '128px' });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const element = container.current;
    if (!element || !visible) return;
    let disposed = false;
    let root: THREE.Object3D | undefined;
    let updateSky: ((deltaSeconds: number) => void) | undefined;
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
      `${label} interactive model preview. Right-drag to pan, use arrow keys to orbit, plus and minus to zoom, and Home to reset.`,
    );
    element.append(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x151922);
    scene.add(new THREE.HemisphereLight(0xffffff, 0x5c6370, 2));
    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 10_000);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.zoomSpeed = 0.5;
    const clock = new THREE.Clock();
    let animationFrame = 0;
    const render = () => {
      animationFrame = 0;
      updateSky?.(Math.min(clock.getDelta(), 0.05));
      controls.update();
      renderer.render(scene, camera);
      if (updateSky) invalidate();
    };
    const invalidate = () => {
      if (!animationFrame) animationFrame = requestAnimationFrame(render);
    };
    controls.addEventListener('change', invalidate);
    const resize = () => {
      const width = Math.max(1, element.clientWidth);
      const height = Math.max(1, element.clientHeight);
      renderer.setSize(width, height, false);
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      invalidate();
    };
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(element);

    const frame = () => {
      if (!root) return;
      frameCameraOnObject(camera, root, controls.target);
      controls.update();
      invalidate();
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
      invalidate();
      event.preventDefault();
    };
    renderer.domElement.addEventListener('keydown', keyDown);
    void window.forge.getAssetPreview(assetId, kind, requestToken, shellIndex, customView).then((source) => {
      if (disposed) return;
      onSource?.(source);
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
      if (kind === 'sky') updateSky = configureSkybox(root).update;
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
      controls.removeEventListener('change', invalidate);
      if (animationFrame) cancelAnimationFrame(animationFrame);
      renderer.domElement.removeEventListener('keydown', keyDown);
      controls.dispose();
      if (root) disposeObject(root);
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
    };
  }, [assetId, customView, kind, label, onSource, previewRevision, retry, shellIndex, visible]);

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
      aria-label={state === 'error' ? 'Retry model preview' : 'Reset model preview view'}
      className="asset-model-preview-reset"
      disabled={state === 'loading'}
      title={state === 'error' ? 'Retry preview' : 'Reset view'}
      variant="default"
      onClick={() => state === 'error' ? setRetry((value) => value + 1) : resetView.current()}
    >
      <ArrowCounterClockwiseIcon />
    </ActionIcon>
  </div>;
}
