// Backlight color helpers mirroring src/VuStudio.Core/Lighting/Colors.cs and Backlight.cs.
import type { BacklightConfig, GradientStop, LedLevels, RgbwHex } from '@/api/types';

/** RGB 0..255 plus the separate white LED 0..255. */
export interface Rgbw {
  r: number;
  g: number;
  b: number;
  w: number;
}

export const BLACK: Rgbw = { r: 0, g: 0, b: 0, w: 0 };

const clampByte = (v: number) => Math.max(0, Math.min(255, Math.round(v)));
const hex2 = (v: number) => clampByte(v).toString(16).padStart(2, '0').toUpperCase();

/** Parses "#RGB", "#RRGGBB" or "#RRGGBB+WW" (leading '#' optional). */
export function parseRgbw(text: string | null | undefined): Rgbw | null {
  if (!text) return null;
  let s = text.trim().replace(/^#/, '');
  let w = 0;
  const plus = s.indexOf('+');
  if (plus >= 0) {
    const white = s.slice(plus + 1);
    if (!/^[0-9a-f]{1,2}$/i.test(white)) return null;
    w = parseInt(white, 16);
    s = s.slice(0, plus);
  }
  if (s.length === 3) s = s.replace(/(.)/g, '$1$1');
  if (!/^[0-9a-f]{6}$/i.test(s)) return null;
  const rgb = parseInt(s, 16);
  return { r: (rgb >> 16) & 255, g: (rgb >> 8) & 255, b: rgb & 255, w };
}

/** "#RRGGBB", or "#RRGGBB+WW" when the white LED is on (same as RgbwColor.ToHex). */
export function formatRgbw(c: Rgbw): RgbwHex {
  const rgb = `#${hex2(c.r)}${hex2(c.g)}${hex2(c.b)}`;
  return c.w > 0 ? `${rgb}+${hex2(c.w)}` : rgb;
}

/** The RGB part only, "#RRGGBB". */
export function rgbHex(c: Rgbw): string {
  return `#${hex2(c.r)}${hex2(c.g)}${hex2(c.b)}`;
}

/** White LED as a percentage (0..100). */
export const whitePercent = (c: Rgbw) => Math.round((c.w / 255) * 100);

export const withWhitePercent = (c: Rgbw, percent: number): Rgbw => ({ ...c, w: clampByte((percent / 100) * 255) });

/** Device levels (0..100 per channel) at a brightness 0..1 (RgbwColor.ToLevels). */
export function toLevels(c: Rgbw, brightness = 1): LedLevels {
  const k = Math.max(0, Math.min(1, brightness));
  const level = (v: number) => Math.max(0, Math.min(100, Math.round((v / 255) * 100 * k)));
  return { r: level(c.r), g: level(c.g), b: level(c.b), w: level(c.w) };
}

/** Approximate RGB of a lit backlight (LedLevels.ToPreviewColor): the white LED adds to every channel. */
export function levelsToPreview(levels: LedLevels): string {
  const white = levels.w * 0.8;
  const ch = (v: number) => hex2(Math.min(255, ((v + white) / 100) * 255));
  return `#${ch(levels.r)}${ch(levels.g)}${ch(levels.b)}`;
}

/** What an RGBW color looks like when lit at full brightness, as "#RRGGBB" (for swatches and bars). */
export function rgbwPreview(color: RgbwHex | Rgbw | null | undefined, brightness = 1): string {
  const c = typeof color === 'string' || color == null ? parseRgbw(color) : color;
  return c ? levelsToPreview(toLevels(c, brightness)) : '#000000';
}

export function isDark(levels: LedLevels | undefined): boolean {
  return !levels || (levels.r === 0 && levels.g === 0 && levels.b === 0 && levels.w === 0);
}

function lerp(a: Rgbw, b: Rgbw, t: number): Rgbw {
  const k = Math.max(0, Math.min(1, t));
  const mix = (x: number, y: number) => Math.round(x + (y - x) * k);
  return { r: mix(a.r, b.r), g: mix(a.g, b.g), b: mix(a.b, b.b), w: mix(a.w, b.w) };
}

/** Color along a gradient at position 0..1 (BacklightEngine.SampleGradient). */
export function sampleGradient(stops: GradientStop[], position: number, fallback: Rgbw = BLACK): Rgbw {
  const ordered = stops
    .map((s) => ({ position: s.position, color: parseRgbw(s.color) ?? BLACK }))
    .sort((a, b) => a.position - b.position);
  if (ordered.length === 0) return fallback;
  const p = Math.max(0, Math.min(1, position));
  if (p <= ordered[0].position) return ordered[0].color;
  for (let i = 1; i < ordered.length; i++) {
    if (p <= ordered[i].position) {
      const a = ordered[i - 1];
      const b = ordered[i];
      const span = b.position - a.position;
      return span <= 0 ? b.color : lerp(a.color, b.color, (p - a.position) / span);
    }
  }
  return ordered[ordered.length - 1].color;
}

/**
 * Base color of a page's light before effects and alarms (BacklightEngine.BaseColor).
 * `syncColor` is the color of the sync source when known.
 */
export function baseColor(config: BacklightConfig, value: number | undefined, fraction: number, syncColor?: Rgbw | null): Rgbw {
  const fallback = parseRgbw(config.color) ?? BLACK;
  switch (config.mode) {
    case 'off':
      return BLACK;
    case 'fixed':
      return fallback;
    case 'gradient':
      return sampleGradient(config.gradient, fraction, fallback);
    case 'thresholds': {
      if (value === undefined || config.thresholds.length === 0) return fallback;
      let chosen = fallback;
      for (const step of [...config.thresholds].sort((a, b) => a.value - b.value)) {
        if (value >= step.value) chosen = parseRgbw(step.color) ?? chosen;
      }
      return chosen;
    }
    case 'sync':
      return syncColor ?? fallback;
    default:
      return fallback;
  }
}

/** CSS linear-gradient for a list of stops, as the light would look (white LED blended in). */
export function gradientCss(stops: GradientStop[], direction = '90deg'): string {
  const ordered = [...stops].sort((a, b) => a.position - b.position);
  if (ordered.length === 0) return '#000';
  if (ordered.length === 1) return rgbwPreview(ordered[0].color);
  return `linear-gradient(${direction}, ${ordered.map((s) => `${rgbwPreview(s.color)} ${(s.position * 100).toFixed(1)}%`).join(', ')})`;
}

/**
 * The "paper" of a lit e-paper face: the backlight color screened over dim ambient light, so a
 * face stays faintly readable when the light is very dim. `null` = light off → dark grey panel.
 */
export function paperColor(previewHex: string | null | undefined): string {
  const c = parseRgbw(previewHex ?? undefined);
  const ambient = 0x30 / 255;
  if (!c || (c.r === 0 && c.g === 0 && c.b === 0)) return '#303236';
  const screen = (v: number) => hex2((1 - (1 - v / 255) * (1 - ambient)) * 255);
  return `#${screen(c.r)}${screen(c.g)}${screen(c.b)}`;
}

/** Relative luminance 0..1 of "#RRGGBB" (for choosing readable text on swatches). */
export function luminance(hex: string): number {
  const c = parseRgbw(hex);
  if (!c) return 0;
  const lin = (v: number) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * lin(c.r) + 0.7152 * lin(c.g) + 0.0722 * lin(c.b);
}
