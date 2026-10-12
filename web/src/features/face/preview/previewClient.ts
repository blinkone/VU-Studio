import { api } from '@/api/client';
import type { DialPage } from '@/api/types';

/** Body of POST /api/faces/preview. */
export interface PreviewRequest {
  slot: number;
  page: DialPage;
  scale: number;
}

/** Thrown when the service answers with an empty image (no face renderer available). */
export class PreviewUnavailableError extends Error {}

/**
 * Renders a page's face to an object URL. Like api.blobUrl, plus cancellation (AbortSignal) and
 * detection of empty responses. The caller owns the URL and must revoke it.
 */
export async function fetchPreviewUrl(request: PreviewRequest, signal?: AbortSignal): Promise<string> {
  const response = await fetch('/api/faces/preview', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', 'X-VuStudio': '1' },
    body: JSON.stringify(request),
    signal,
  });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  const blob = await response.blob();
  if (blob.size === 0) throw new PreviewUnavailableError('empty preview');
  return URL.createObjectURL(blob);
}

// ---------------------------------------------------------------- theme thumbnails

/** Small LRU of object URLs; evicted URLs are revoked. */
class UrlCache {
  private readonly map = new Map<string, string>();

  constructor(private readonly capacity: number) {}

  get(key: string): string | undefined {
    const url = this.map.get(key);
    if (url !== undefined) {
      this.map.delete(key);
      this.map.set(key, url);
    }
    return url;
  }

  set(key: string, url: string) {
    const old = this.map.get(key);
    if (old !== undefined && old !== url) URL.revokeObjectURL(old);
    this.map.delete(key);
    this.map.set(key, url);
    while (this.map.size > this.capacity) {
      const [oldestKey, oldestUrl] = this.map.entries().next().value as [string, string];
      this.map.delete(oldestKey);
      URL.revokeObjectURL(oldestUrl);
    }
  }
}

const thumbnails = new UrlCache(96);
const failures = new Map<string, number>();
const inFlight = new Map<string, Promise<string>>();
const FAILURE_TTL_MS = 15_000;
const MAX_CONCURRENT = 3;

let active = 0;
const waiting: (() => void)[] = [];

async function withSlot<T>(job: () => Promise<T>): Promise<T> {
  if (active >= MAX_CONCURRENT) await new Promise<void>((resolve) => waiting.push(resolve));
  active++;
  try {
    return await job();
  } finally {
    active--;
    waiting.shift()?.();
  }
}

export function cachedThumbnail(key: string): string | undefined {
  return thumbnails.get(key);
}

export function thumbnailFailedRecently(key: string): boolean {
  const at = failures.get(key);
  return at !== undefined && Date.now() - at < FAILURE_TTL_MS;
}

/** Fetches (or reuses) the thumbnail for `key`; requests are deduplicated and at most 3 run at once. */
export function loadThumbnail(key: string, request: PreviewRequest): Promise<string> {
  const cached = thumbnails.get(key);
  if (cached) return Promise.resolve(cached);
  const running = inFlight.get(key);
  if (running) return running;
  const promise = withSlot(async () => {
    const url = await api.blobUrl('/api/faces/preview', request);
    // The placeholder renderer answers with an empty PNG: treat it as "no preview".
    const probe = await fetch(url);
    if ((await probe.blob()).size === 0) {
      URL.revokeObjectURL(url);
      throw new PreviewUnavailableError('empty preview');
    }
    thumbnails.set(key, url);
    failures.delete(key);
    return url;
  })
    .catch((error: unknown) => {
      failures.set(key, Date.now());
      throw error;
    })
    .finally(() => inFlight.delete(key));
  inFlight.set(key, promise);
  return promise;
}
