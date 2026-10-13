import { formatNumber } from '@/lib/format';

export interface ReadingParts {
  /** Number (or text) to show; '–' when there is no value. */
  value: string;
  /** Unit shown next to the value ('' when none). */
  unit: string;
  /** True when there is no reading at all. */
  empty: boolean;
}

/** Splits a reading that is already in display units into number and unit text. */
export function readingParts(
  value: number | null | undefined,
  unit: string | undefined,
  text?: string | null,
  locale?: string,
  decimals?: number,
): ReadingParts {
  if (text) return { value: text, unit: '', empty: false };
  if (value === null || value === undefined || !Number.isFinite(value)) return { value: '–', unit: '', empty: true };
  return { value: formatNumber(value, decimals, locale), unit: unit ?? '', empty: false };
}

/** Single-line reading, e.g. "42.5%", "61 °C", "12.3 MB/s". */
export function formatReading(
  value: number | null | undefined,
  unit: string | undefined,
  text?: string | null,
  locale?: string,
  decimals?: number,
): string {
  const parts = readingParts(value, unit, text, locale, decimals);
  if (!parts.unit) return parts.value;
  return parts.unit === '%' || parts.unit.startsWith('°') ? `${parts.value}${parts.unit}` : `${parts.value} ${parts.unit}`;
}

/** URL of the face currently shown on a slot (3× upscaled; `v` busts the cache when the face changes). */
export const faceImageUrl = (slot: number, faceVersion: number) => `/api/dials/${slot}/face.png?scale=3&v=${faceVersion}`;
