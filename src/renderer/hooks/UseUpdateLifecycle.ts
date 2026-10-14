import { useCallback, useEffect, useRef, useState } from 'react';

import type { UpdateLifecycleAction, UpdateLifecycleSnapshot } from '../../types/Updates.js';

export function useUpdateLifecycle() {
  const [snapshot, setSnapshot] = useState<UpdateLifecycleSnapshot>();
  const receivedEvent = useRef(false);

  useEffect(() => {
    let mounted = true;
    const unsubscribe = window.forge.onUpdateLifecycleChanged((value) => {
      receivedEvent.current = true;
      setSnapshot(value);
    });
    void window.forge.getUpdateLifecycle().then((value) => {
      if (mounted && !receivedEvent.current) setSnapshot(value);
    }).catch(() => undefined);
    return () => {
      mounted = false;
      unsubscribe();
    };
  }, []);

  const runAction = useCallback((action: UpdateLifecycleAction) => window.forge.runUpdateAction(action), []);
  return { snapshot, runAction };
}
