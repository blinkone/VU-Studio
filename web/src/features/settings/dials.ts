import { useMemo } from 'react';
import { useLive } from '@/api/live';
import type { DialInfo, DialSettings, Easing } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';

// A physical dial is known by its UID. The hardware layer reports which ones are connected right
// now; the configuration remembers a slot (position used by profiles) and a name for each.

/** One line of the dial table: what is connected now merged with what the configuration remembers. */
export interface DialRow {
  uid: string;
  /** Detected by the hub right now (may be offline). */
  info?: DialInfo;
  /** Entry in the configuration, if the dial has one yet. */
  settings?: DialSettings;
  slot?: number;
  name?: string;
  online: boolean;
}

const sameUid = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

export function mergeDials(hardware: readonly DialInfo[], settings: readonly DialSettings[]): DialRow[] {
  const rows: DialRow[] = settings.map((s) => {
    const info = hardware.find((h) => sameUid(h.uid, s.uid));
    return { uid: s.uid, info, settings: s, slot: s.slot, name: s.name, online: info?.online ?? false };
  });
  for (const info of hardware) {
    if (!rows.some((r) => sameUid(r.uid, info.uid))) rows.push({ uid: info.uid, info, online: info.online });
  }
  return rows.sort(
    (a, b) =>
      (a.slot ?? Number.MAX_SAFE_INTEGER) - (b.slot ?? Number.MAX_SAFE_INTEGER) ||
      (a.info?.busIndex ?? 999) - (b.info?.busIndex ?? 999) ||
      a.uid.localeCompare(b.uid),
  );
}

/** Dial rows from the live hardware list and the configuration draft. */
export function useDialRows(): DialRow[] {
  // Every state message carries a fresh `hardware` array; compare by content so the dial table does
  // not re-render ten times a second.
  const hardwareJson = useLive((s) => JSON.stringify(s.state?.hardware ?? []));
  const dials = useConfigDraft((s) => s.draft?.hardware.dials);
  return useMemo(() => mergeDials(JSON.parse(hardwareJson) as DialInfo[], dials ?? []), [hardwareJson, dials]);
}

export function nextFreeSlot(dials: readonly DialSettings[]): number {
  let slot = 1;
  while (dials.some((d) => d.slot === slot)) slot++;
  return slot;
}

/** Finds the configuration entry of a dial, creating it (in the first free slot) when it has none. */
export function ensureDial(dials: DialSettings[], uid: string): DialSettings {
  const existing = dials.find((d) => sameUid(d.uid, uid));
  if (existing) return existing;
  const created: DialSettings = { uid, slot: nextFreeSlot(dials) };
  dials.push(created);
  return created;
}

/** Moves a dial to a slot. When another dial already has it, the two dials trade places. */
export function setDialSlot(dials: DialSettings[], uid: string, slot: number): void {
  const dial = ensureDial(dials, uid);
  const other = dials.find((d) => d !== dial && d.slot === slot);
  if (other) other.slot = dial.slot;
  dial.slot = slot;
}

// ---------------------------------------------------------------------------- easing presets

export type EasingKind = 'needle' | 'backlight';
export type EasingPreset = 'fast' | 'normal' | 'lazy';

/** On-device easing: the dial moves `step` % every `periodMs` ms. "Normal" is the factory default. */
export const EASING_PRESETS: Record<EasingKind, Record<EasingPreset, Easing>> = {
  needle: {
    fast: { step: 10, periodMs: 20 },
    normal: { step: 2, periodMs: 50 },
    lazy: { step: 1, periodMs: 50 },
  },
  backlight: {
    fast: { step: 10, periodMs: 20 },
    normal: { step: 5, periodMs: 100 },
    lazy: { step: 1, periodMs: 50 },
  },
};

export const PRESET_ORDER: readonly EasingPreset[] = ['fast', 'normal', 'lazy'];

/** Which preset an easing corresponds to (no easing = factory default = "normal"). */
export function matchPreset(kind: EasingKind, easing: Easing | undefined): EasingPreset | 'custom' {
  const current = easing ?? EASING_PRESETS[kind].normal;
  return PRESET_ORDER.find((p) => EASING_PRESETS[kind][p].step === current.step && EASING_PRESETS[kind][p].periodMs === current.periodMs) ?? 'custom';
}

/** Full-scale speed in % per second. */
export const easingSpeed = (easing: Easing): number => Math.round((easing.step * 1000) / Math.max(1, easing.periodMs));
