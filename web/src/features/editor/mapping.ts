// Client mirror of src/VuStudio.Core/Mapping/ValueMapping.cs (value in display units → dial fraction).
import type { ScalePoint, ScaleType } from '@/api/types';

const DECADES = 3;

export interface Mapping {
  min: number;
  max: number;
  scale: ScaleType;
  points?: ScalePoint[];
  invert: boolean;
}

/** Converts a value to a dial fraction 0..1, exactly like the engine does for the needle. */
export function toFraction(value: number, mapping: Mapping): number {
  let { min, max, scale } = mapping;
  if (max === min) max = min + 1;
  const invert = mapping.invert !== max < min;
  const lo = Math.min(min, max);
  const hi = Math.max(min, max);
  let points: ScalePoint[] | undefined;
  let low = lo;
  let high = hi;

  if (scale === 'custom') {
    const ordered = (mapping.points ?? []).filter((p) => Number.isFinite(p.value) && Number.isFinite(p.position)).sort((a, b) => a.value - b.value);
    if (ordered.length < 2) {
      scale = 'linear';
    } else {
      points = ordered;
      low = ordered[0].value;
      high = ordered[ordered.length - 1].value;
    }
  }

  if (Number.isNaN(value)) return invert ? 1 : 0;
  const range = high - low || 1;
  let f: number;
  switch (scale) {
    case 'logarithmic': {
      const eps = range / 10 ** DECADES;
      const v = Math.max(0, value - low);
      f = Math.log10(1 + v / eps) / Math.log10(1 + range / eps);
      break;
    }
    case 'squareRoot':
      f = Math.sqrt(Math.max(0, value - low) / range);
      break;
    case 'custom':
      f = customFraction(value, points!);
      break;
    default:
      f = (value - low) / range;
  }
  f = Math.max(0, Math.min(1, f));
  return invert ? 1 - f : f;
}

function customFraction(value: number, pts: ScalePoint[]): number {
  if (value <= pts[0].value) return pts[0].position;
  for (let i = 1; i < pts.length; i++) {
    if (value <= pts[i].value) {
      const a = pts[i - 1];
      const b = pts[i];
      const t = (value - a.value) / (b.value - a.value);
      return a.position + t * (b.position - a.position);
    }
  }
  return pts[pts.length - 1].position;
}
