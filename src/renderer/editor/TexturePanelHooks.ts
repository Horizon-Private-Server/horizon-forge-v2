import type { SyntheticEvent } from 'react';
import { useCallback, useEffect, useRef, useState } from 'react';

import { errorMessage } from '../../utils/Errors.ts';
import { AssetThumbnailRuntime } from './AssetThumbnailRuntime.ts';

export function useAssetThumbnailRuntime(targetGame: string): AssetThumbnailRuntime | null | undefined {
  const [runtime, setRuntime] = useState<AssetThumbnailRuntime | null>();
  useEffect(() => {
    try {
      const value = new AssetThumbnailRuntime(targetGame);
      setRuntime(value);
      return () => value.dispose();
    } catch {
      setRuntime(null);
    }
  }, [targetGame]);
  return runtime;
}

export function useVirtualGridViewport(element: HTMLDivElement | null) {
  const [viewport, setViewport] = useState({ width: 0, height: 0, scrollTop: 0 });
  useEffect(() => {
    if (!element) return;
    let frame = 0;
    const update = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        setViewport({ width: element.clientWidth, height: element.clientHeight, scrollTop: element.scrollTop });
      });
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    element.addEventListener('scroll', update, { passive: true });
    return () => {
      observer.disconnect();
      element.removeEventListener('scroll', update);
      if (frame) cancelAnimationFrame(frame);
    };
  }, [element]);
  return viewport;
}

export function useTextureCardThumbnail(
  root: HTMLElement | null,
  runtime: AssetThumbnailRuntime | null | undefined,
  assetId?: string,
) {
  const element = useRef<HTMLDivElement>(null);
  const [thumbnail, setThumbnail] = useState<string>();
  const [failure, setFailure] = useState('');
  const [dimensions, setDimensions] = useState<{ width: number; height: number }>();
  useEffect(() => {
    setThumbnail(undefined);
    setFailure('');
    setDimensions(undefined);
    if (!root || !runtime || !assetId) return;
    const cancellation = new AbortController();
    const observer = new IntersectionObserver((entries) => {
      if (!entries.some((entry) => entry.isIntersecting)) return;
      observer.disconnect();
      void runtime.getTextureSource(assetId, cancellation.signal)
        .then(setThumbnail)
        .catch((cause: unknown) => {
          if (!(cause instanceof DOMException && cause.name === 'AbortError')) setFailure(errorMessage(cause));
        });
    }, { root, rootMargin: '284px 0px' });
    if (element.current) observer.observe(element.current);
    return () => { observer.disconnect(); cancellation.abort(); };
  }, [assetId, root, runtime]);
  const onImageLoad = useCallback((event: SyntheticEvent<HTMLImageElement>) => {
    setDimensions({ width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight });
  }, []);
  return { dimensions, element, failure, onImageLoad, thumbnail };
}
