import { useEffect, useMemo, useRef, useState } from 'react';
import type { DialPage } from '@/api/types';
import { fetchPreviewUrl, PreviewUnavailableError, type PreviewRequest } from './previewClient';

export type PreviewStatus = 'loading' | 'ready' | 'unavailable' | 'error';

export interface LivePreview {
  url: string | null;
  status: PreviewStatus;
  /** A newer preview is being rendered (the current image is slightly stale). */
  busy: boolean;
}

const DEBOUNCE_MS = 150;
/** Continuous edits (dragging) still refresh at least this often. */
const MAX_WAIT_MS = 320;
/** Live values, clocks and sparklines change on their own: re-render periodically. */
const REFRESH_MS = 5_000;

/**
 * Pixel-exact preview of a page that is not saved yet (POST /api/faces/preview). Requests are debounced,
 * stale ones are aborted, and the previous image stays visible until the next one has been decoded.
 */
export function useLivePreview(slot: number, page: DialPage, scale = 4): LivePreview {
  const body = useMemo<PreviewRequest>(() => ({ slot, page, scale }), [slot, page, scale]);
  const key = useMemo(() => JSON.stringify(body), [body]);
  const [preview, setPreview] = useState<LivePreview>({ url: null, status: 'loading', busy: true });
  const [tick, setTick] = useState(0);
  const latest = useRef({ key, body });
  const shownUrl = useRef<string | null>(null);
  const controller = useRef<AbortController | null>(null);
  const firstChangeAt = useRef<number | null>(null);

  useEffect(() => {
    latest.current = { key, body };
  }, [key, body]);

  useEffect(() => {
    const id = window.setInterval(() => {
      if (document.visibilityState === 'visible') setTick((t) => t + 1);
    }, REFRESH_MS);
    return () => window.clearInterval(id);
  }, []);

  useEffect(() => {
    const now = Date.now();
    firstChangeAt.current ??= now;
    const waited = now - firstChangeAt.current;
    const delay = Math.max(0, Math.min(DEBOUNCE_MS, MAX_WAIT_MS - waited));
    setPreview((p) => (p.busy ? p : { ...p, busy: true }));

    const timer = window.setTimeout(() => {
      firstChangeAt.current = null;
      controller.current?.abort();
      const abort = new AbortController();
      controller.current = abort;
      const requestKey = latest.current.key;
      fetchPreviewUrl(latest.current.body, abort.signal)
        .then(async (url) => {
          // Decode before swapping so the stage never flashes an empty frame.
          const img = new Image();
          img.src = url;
          await img.decode().catch(() => undefined);
          if (abort.signal.aborted) {
            URL.revokeObjectURL(url);
            return;
          }
          const previous = shownUrl.current;
          shownUrl.current = url;
          setPreview({ url, status: 'ready', busy: latest.current.key !== requestKey });
          if (previous) window.setTimeout(() => URL.revokeObjectURL(previous), 1000);
        })
        .catch((error: unknown) => {
          if (abort.signal.aborted) return;
          const status: PreviewStatus = error instanceof PreviewUnavailableError ? 'unavailable' : 'error';
          setPreview((p) => ({ url: p.url, status: p.url && status === 'error' ? 'ready' : status, busy: false }));
        });
    }, delay);

    return () => window.clearTimeout(timer);
  }, [key, tick]);

  useEffect(
    () => () => {
      controller.current?.abort();
      if (shownUrl.current) URL.revokeObjectURL(shownUrl.current);
      shownUrl.current = null;
    },
    [],
  );

  return preview;
}
