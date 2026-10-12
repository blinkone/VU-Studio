import type { BacklightConfig, GradientStop, RgbwHex } from '@/api/types';

// Backlight colors for the preview bezel. The e-paper is black ink on white paper lit from behind, so
// the preview multiplies the 1-bit image with the backlight color (white paper → tint, ink stays black).

export interface Rgb {
  r: number;
  g: number;
  b: number;
}

/** Unlit e-paper: a neutral light grey. */
export const PAPER_OFF = '#d5d6d1';

const clamp255 = (v: number) => Math.max(0, Math.min(255, Math.round(v)));

/** Parses "#RRGGBB" or "#RRGGBB+WW"; the white LED is mixed in like the service's preview color. */
export function parseRgbw(hex: RgbwHex | undefined): Rgb | null {
  if (!hex) return null;
  const m = /^#?([0-9a-f]{6})(?:\+([0-9a-f]{2}))?$/i.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  const white = m[2] ? parseInt(m[2], 16) * 0.8 : 0;
  return { r: clamp255(((n >> 16) & 255) + white), g: clamp255(((n >> 8) & 255) + white), b: clamp255((n & 255) + white) };
}

export function toHex({ r, g, b }: Rgb): string {
  return `#${[r, g, b].map((v) => clamp255(v).toString(16).padStart(2, '0')).join('')}`;
}

function mix(a: Rgb, b: Rgb, t: number): Rgb {
  return { r: a.r + (b.r - a.r) * t, g: a.g + (b.g - a.g) * t, b: a.b + (b.b - a.b) * t };
}

/**
 * Makes a backlight color readable as lit paper: brightness is normalised (a dimmed backlight still
 * reads as its hue) and a little white is mixed in, like light diffused through the e-paper.
 */
export function litPaper(color: Rgb | null): string {
  if (!color) return PAPER_OFF;
  const max = Math.max(color.r, color.g, color.b);
  if (max < 8) return PAPER_OFF;
  const scaled = { r: (color.r * 255) / max, g: (color.g * 255) / max, b: (color.b * 255) / max };
  return toHex(mix(scaled, { r: 255, g: 255, b: 255 }, 0.3));
}

export function gradientAt(stops: GradientStop[], position: number): Rgb | null {
  const parsed = stops
    .map((s) => ({ position: s.position, color: parseRgbw(s.color) }))
    .filter((s): s is { position: number; color: Rgb } => s.color !== null)
    .sort((a, b) => a.position - b.position);
  if (parsed.length === 0) return null;
  if (position <= parsed[0].position) return parsed[0].color;
  for (let i = 1; i < parsed.length; i++) {
    const a = parsed[i - 1];
    const b = parsed[i];
    if (position <= b.position) {
      const span = b.position - a.position;
      return span <= 0 ? b.color : mix(a.color, b.color, (position - a.position) / span);
    }
  }
  return parsed[parsed.length - 1].color;
}

/**
 * Approximate backlight color of a page with the needle at `fraction`, for previews of pages that are
 * not live. `valueAt` converts a fraction to a display value (for threshold mode).
 */
export function backlightAt(light: BacklightConfig, fraction: number, valueAt: (f: number) => number): Rgb | null {
  switch (light.mode) {
    case 'off':
      return null;
    case 'gradient':
      return gradientAt(light.gradient, fraction) ?? parseRgbw(light.color);
    case 'thresholds': {
      const value = valueAt(fraction);
      let color = parseRgbw(light.color);
      for (const step of [...light.thresholds].sort((a, b) => a.value - b.value)) {
        if (value >= step.value) color = parseRgbw(step.color) ?? color;
      }
      return color;
    }
    default:
      return parseRgbw(light.color);
  }
}
