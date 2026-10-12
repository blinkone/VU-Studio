import { useEffect, useMemo, useState } from 'react';
import { api } from '@/api/client';
import { useLiveValue } from '@/api/hooks';
import { useSensors } from '@/api/queries';
import type { DialPage, GeneralSettings, LiveValue, SensorDto } from '@/api/types';
import { baseColor, levelsToPreview, parseRgbw, toLevels } from '@/features/dial/color';
import { toFraction } from './mapping';
import { convertUnit, resolveRange, type ResolvedRange } from './units';

export interface PageReading {
  sensor: SensorDto | undefined;
  range: ResolvedRange;
  /** Live sample of the sensor (canonical units). */
  live: LiveValue | undefined;
  /** Value in display units, when known. */
  display: number | undefined;
  text: string | undefined;
  /** Needle position 0..1 for the current value with the page's (possibly unsaved) mapping. */
  fraction: number;
  stale: boolean;
}

/** The selected sensor's live value, mapped with the page's current settings. */
export function usePageReading(page: DialPage | undefined, general: GeneralSettings | undefined): PageReading {
  const { data: sensors } = useSensors();
  const sensorId = page?.value.sensorId;
  const sensor = useMemo(() => sensors?.find((s) => s.id === sensorId), [sensors, sensorId]);
  const live = useLiveValue(sensorId);
  const value = page?.value;

  return useMemo(() => {
    const binding = value ?? { displayUnit: 'auto', scale: 'linear' as const, invert: false, attackMs: 0, releaseMs: 0, holdMs: 0, offline: 'zero' as const };
    const range = resolveRange(binding, sensor, general);
    const raw = live ? (live.v ?? undefined) : sensor?.value;
    const text = live ? (live.t ?? undefined) : sensor?.text;
    const stale = live ? live.s : (sensor?.stale ?? false);
    const display = raw !== undefined && raw !== null && !stale ? convertUnit(raw, range.canonical, range.unit) : undefined;
    const fraction =
      display !== undefined
        ? toFraction(display, { min: binding.min ?? range.suggestedMin, max: binding.max ?? range.suggestedMax, scale: binding.scale, points: binding.points, invert: binding.invert })
        : 0;
    return { sensor, range, live, display, text: stale ? undefined : text, fraction, stale };
  }, [value, sensor, general, live]);
}

const ACCENT_SENSOR = 'session/accent';
const ALBUM_SENSOR = 'session/media/color';

/** Approximate backlight color ("#RRGGBB", null = off) of a page for its current value. */
export function usePageLightPreview(page: DialPage | undefined, reading: PageReading): string | null {
  const light = page?.light;
  const syncId =
    light?.mode === 'sync' ? (light.sync === 'accentColor' ? ACCENT_SENSOR : light.sync === 'albumArt' ? ALBUM_SENSOR : light.syncSensorId) : undefined;
  const sync = useLiveValue(syncId);
  if (!light || light.mode === 'off') return null;
  const color = baseColor(light, reading.display, reading.fraction, parseRgbw(sync?.t ?? undefined));
  const levels = toLevels(color, light.brightness / 100);
  const hex = levelsToPreview(levels);
  return hex === '#000000' ? null : hex;
}

/**
 * Pixel-exact face preview of a (possibly unsaved) page from POST /api/faces/preview, as an object
 * URL. Requests are debounced while the user types; stale URLs are revoked.
 */
export function useFacePreview(slot: number, page: DialPage | undefined, enabled: boolean): string | undefined {
  const [url, setUrl] = useState<string>();
  const key = enabled && page ? JSON.stringify({ value: page.value, face: page.face }) : null;

  useEffect(() => {
    if (!key || !page) return;
    let cancelled = false;
    let created: string | undefined;
    const timer = setTimeout(() => {
      api.blobUrl('/api/faces/preview', { slot, page, scale: 3 }).then(
        (u) => {
          if (cancelled) {
            URL.revokeObjectURL(u);
            return;
          }
          created = u;
          setUrl(u);
        },
        () => undefined,
      );
    }, 350);
    return () => {
      cancelled = true;
      clearTimeout(timer);
      // Keep the old image alive while the dial cross-fades to the next one.
      if (created) {
        const old = created;
        setTimeout(() => URL.revokeObjectURL(old), 2000);
      }
    };
    // `page` is captured through `key`.
  }, [key, slot]);

  return enabled ? url : undefined;
}
