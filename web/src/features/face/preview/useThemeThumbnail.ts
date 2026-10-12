import { useEffect, useState } from 'react';
import { useDebouncedValue } from '@mantine/hooks';
import { cachedThumbnail, loadThumbnail, thumbnailFailedRecently, type PreviewRequest } from './previewClient';

export interface Thumbnail {
  url: string | null;
  status: 'idle' | 'loading' | 'ready' | 'unavailable';
}

/**
 * Lazily rendered theme preview. Nothing is requested until `visible` turns true; later changes of
 * `request` are debounced, and the previous image stays up until the new one is ready.
 */
export function useThemeThumbnail(key: string, request: PreviewRequest, visible: boolean): Thumbnail {
  const [debouncedKey] = useDebouncedValue(key, 450, { leading: true });
  const [state, setState] = useState<Thumbnail>(() => {
    const url = cachedThumbnail(key);
    return url ? { url, status: 'ready' } : { url: null, status: 'idle' };
  });
  // The request matching the debounced key (kept in state so the effect below sees a consistent pair).
  const [pending, setPending] = useState({ key, request });

  useEffect(() => {
    if (debouncedKey === key) setPending((p) => (p.key === key ? p : { key, request }));
  }, [debouncedKey, key, request]);

  useEffect(() => {
    if (!visible) return;
    let cancelled = false;
    const cached = cachedThumbnail(pending.key);
    if (cached) {
      setState({ url: cached, status: 'ready' });
      return;
    }
    if (thumbnailFailedRecently(pending.key)) {
      setState((s) => (s.url ? s : { url: null, status: 'unavailable' }));
      return;
    }
    setState((s) => ({ url: s.url, status: 'loading' }));
    loadThumbnail(pending.key, pending.request)
      .then((url) => {
        if (!cancelled) setState({ url, status: 'ready' });
      })
      .catch(() => {
        if (!cancelled) setState((s) => (s.url ? { url: s.url, status: 'ready' } : { url: null, status: 'unavailable' }));
      });
    return () => {
      cancelled = true;
    };
  }, [pending, visible]);

  return state;
}
