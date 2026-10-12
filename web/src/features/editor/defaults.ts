// Defaults and presets for new pages, mirroring the C# model defaults (Profiles/Profile.cs,
// Faces/FaceDocument.cs, Lighting/Backlight.cs).
import type { AlarmRule, BacklightConfig, DialAssignment, DialPage, Easing, FaceConfig, GradientStop, ValueBinding } from '@/api/types';
import { newId } from '@/state/configDraft';

export const DEFAULT_ROTATION_SECONDS = 15;

export const DEFAULT_GRADIENT: GradientStop[] = [
  { position: 0, color: '#00C8FF' },
  { position: 0.6, color: '#00FF50' },
  { position: 0.8, color: '#FFD200' },
  { position: 1, color: '#FF0000' },
];

export function defaultValue(): ValueBinding {
  return { displayUnit: 'auto', scale: 'linear', invert: false, attackMs: 150, releaseMs: 300, holdMs: 0, offline: 'zero' };
}

export function defaultFace(): FaceConfig {
  return {
    theme: 'minimal/arc',
    showIcon: true,
    showValue: true,
    showSparkline: false,
    warnFrom: 0.85,
    labelCount: 5,
    inverted: false,
    minRefreshSeconds: 60,
  };
}

export function defaultLight(): BacklightConfig {
  return {
    mode: 'gradient',
    color: '#FFAA5A+78',
    gradient: DEFAULT_GRADIENT.map((s) => ({ ...s })),
    thresholds: [],
    sync: 'accentColor',
    brightness: 80,
    effect: 'none',
    effectPeriodMs: 4000,
    effectDepth: 60,
    alarms: [],
  };
}

/** A complete new page with every field populated. */
export function createPage(name?: string): DialPage {
  return { id: newId(), ...(name ? { name } : {}), value: defaultValue(), face: defaultFace(), light: defaultLight() };
}

export function createAssignment(slot: number): DialAssignment {
  return { slot, rotationSeconds: DEFAULT_ROTATION_SECONDS, pages: [createPage()] };
}

/** Deep copy of a page with a fresh id (ids must be unique within an assignment). */
export function clonePage(page: DialPage): DialPage {
  return { ...structuredClone(page), id: newId() };
}

/** Deep copy of an assignment for another profile, with fresh page ids. */
export function cloneAssignment(assignment: DialAssignment, slot: number): DialAssignment {
  return { slot, rotationSeconds: assignment.rotationSeconds, pages: assignment.pages.map(clonePage) };
}

export function createAlarm(threshold: number): AlarmRule {
  return {
    enabled: true,
    comparison: 'above',
    threshold,
    hysteresis: 0,
    forSeconds: 3,
    color: '#FF0000',
    effect: 'blink',
    periodMs: 800,
    notify: false,
  };
}

// ---------------------------------------------------------------- presets

export type SmoothingPreset = 'snappy' | 'smooth' | 'vu' | 'ppm' | 'custom';

export const SMOOTHING_PRESETS: Record<Exclude<SmoothingPreset, 'custom'>, Pick<ValueBinding, 'attackMs' | 'releaseMs' | 'holdMs'>> = {
  snappy: { attackMs: 40, releaseMs: 80, holdMs: 0 },
  smooth: { attackMs: 150, releaseMs: 300, holdMs: 0 },
  vu: { attackMs: 300, releaseMs: 300, holdMs: 0 },
  ppm: { attackMs: 10, releaseMs: 1500, holdMs: 500 },
};

export function smoothingPresetOf(value: ValueBinding): SmoothingPreset {
  for (const [key, p] of Object.entries(SMOOTHING_PRESETS)) {
    if (p.attackMs === value.attackMs && p.releaseMs === value.releaseMs && p.holdMs === value.holdMs) return key as SmoothingPreset;
  }
  return 'custom';
}

export type NeedleSpeed = 'fast' | 'normal' | 'lazy' | 'device' | 'custom';

export const NEEDLE_SPEEDS: Record<Exclude<NeedleSpeed, 'device' | 'custom'>, Easing> = {
  fast: { step: 10, periodMs: 20 },
  normal: { step: 2, periodMs: 50 },
  lazy: { step: 1, periodMs: 50 },
};

export function needleSpeedOf(easing: Easing | undefined): NeedleSpeed {
  if (!easing) return 'device';
  for (const [key, e] of Object.entries(NEEDLE_SPEEDS)) {
    if (e.step === easing.step && e.periodMs === easing.periodMs) return key as NeedleSpeed;
  }
  return 'custom';
}

/** Ready-made gradients for the light editor. */
export const GRADIENT_PRESETS: Record<string, GradientStop[]> = {
  classic: DEFAULT_GRADIENT,
  traffic: [
    { position: 0, color: '#00FF3C' },
    { position: 0.55, color: '#FFD200' },
    { position: 1, color: '#FF0000' },
  ],
  ice: [
    { position: 0, color: '#FFFFFF+40' },
    { position: 0.5, color: '#00DCFF' },
    { position: 1, color: '#0050FF' },
  ],
  fire: [
    { position: 0, color: '#FFD200' },
    { position: 0.5, color: '#FF7800' },
    { position: 1, color: '#FF0000' },
  ],
  aurora: [
    { position: 0, color: '#00FF8C' },
    { position: 0.5, color: '#00B4FF' },
    { position: 1, color: '#B400FF' },
  ],
};

/** Swatches offered by the color input ("#RRGGBB" or "#RRGGBB+WW"). */
export const COLOR_SWATCHES: string[] = [
  '#FFAA5A+78',
  '#FFFFFF+FF',
  '#FF0000',
  '#FF7800',
  '#FFD200',
  '#00FF3C',
  '#00DCFF',
  '#0050FF',
  '#8C00FF',
  '#FF00B4',
];
