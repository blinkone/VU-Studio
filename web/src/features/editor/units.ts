// Client mirror of src/VuStudio.Core/Mapping/UnitConverter.cs and Engine/BindingResolver.cs, used to
// show the effective unit and range of a page before it is saved.
import type { GeneralSettings, SensorDto, ValueBinding } from '@/api/types';

export const AUTO_UNIT = 'auto';

interface UnitDef {
  family: string;
  factor: number;
  offset?: number;
}

const KB = 1024;
const UNITS: Record<string, UnitDef> = {
  B: { family: 'bytes', factor: 1 },
  KB: { family: 'bytes', factor: KB },
  MB: { family: 'bytes', factor: KB ** 2 },
  GB: { family: 'bytes', factor: KB ** 3 },
  TB: { family: 'bytes', factor: KB ** 4 },
  'B/s': { family: 'rate', factor: 1 },
  'KB/s': { family: 'rate', factor: KB },
  'MB/s': { family: 'rate', factor: KB ** 2 },
  'GB/s': { family: 'rate', factor: KB ** 3 },
  'bit/s': { family: 'rate', factor: 1 / 8 },
  'Kbit/s': { family: 'rate', factor: 1000 / 8 },
  'Mbit/s': { family: 'rate', factor: 1e6 / 8 },
  'Gbit/s': { family: 'rate', factor: 1e9 / 8 },
  '°C': { family: 'temperature', factor: 1 },
  '°F': { family: 'temperature', factor: 5 / 9, offset: (-32 * 5) / 9 },
  K: { family: 'temperature', factor: 1, offset: -273.15 },
  Hz: { family: 'frequency', factor: 1e-6 },
  kHz: { family: 'frequency', factor: 1e-3 },
  MHz: { family: 'frequency', factor: 1 },
  GHz: { family: 'frequency', factor: 1000 },
  ms: { family: 'time', factor: 1 / 1000 },
  s: { family: 'time', factor: 1 },
  min: { family: 'time', factor: 60 },
  h: { family: 'time', factor: 3600 },
  mW: { family: 'power', factor: 1 / 1000 },
  W: { family: 'power', factor: 1 },
  kW: { family: 'power', factor: 1000 },
  Wh: { family: 'energy', factor: 1 },
  kWh: { family: 'energy', factor: 1000 },
  mV: { family: 'voltage', factor: 1 / 1000 },
  V: { family: 'voltage', factor: 1 },
};

export function areCompatible(a: string, b: string): boolean {
  return a === b || (!!UNITS[a] && !!UNITS[b] && UNITS[a].family === UNITS[b].family);
}

/** Converts between compatible units; anything else is returned unchanged. */
export function convertUnit(value: number, from: string, to: string | undefined): number {
  if (!to || to === AUTO_UNIT || from === to) return value;
  const a = UNITS[from];
  const b = UNITS[to];
  if (!a || !b || a.family !== b.family) return value;
  const base = value * a.factor + (a.offset ?? 0);
  return (base - (b.offset ?? 0)) / b.factor;
}

/** Picks a unit of the same family so that the magnitude reads between 1 and 1000. */
export function pickAutoUnit(magnitude: number, canonical: string, preferBits = false): string {
  const unit = UNITS[canonical];
  if (!unit) return canonical;
  const ladders: Record<string, string[]> = {
    bytes: ['B', 'KB', 'MB', 'GB', 'TB'],
    rate: preferBits ? ['bit/s', 'Kbit/s', 'Mbit/s', 'Gbit/s'] : ['B/s', 'KB/s', 'MB/s', 'GB/s'],
    frequency: ['MHz', 'GHz'],
    power: ['W', 'kW'],
    energy: ['Wh', 'kWh'],
  };
  const ladder = ladders[unit.family] ?? [canonical];
  const abs = Math.abs(magnitude);
  let chosen = ladder[0];
  for (const name of ladder) {
    const converted = Math.abs(convertUnit(abs, canonical, name));
    if (converted >= 1 || name === ladder[0]) chosen = name;
    if (converted < 1000) break;
  }
  return chosen;
}

/** The unit the engine will use for a binding ("auto" resolved). */
export function resolveDisplayUnit(binding: ValueBinding, sensor: SensorDto | undefined, general: GeneralSettings | undefined): string {
  const canonical = sensor?.unit ?? '%';
  if (binding.displayUnit && binding.displayUnit !== AUTO_UNIT && areCompatible(canonical, binding.displayUnit)) {
    return binding.displayUnit;
  }
  switch (canonical) {
    case '°C':
      return general?.temperatureUnit === '°F' ? '°F' : '°C';
    case 'B/s':
      return pickAutoUnit(binding.max ?? sensor?.max ?? 125_000_000, canonical, !!general?.networkInBits && sensor?.category === 'network');
    case 'B':
      return pickAutoUnit(binding.max ?? sensor?.max ?? KB ** 3, canonical);
    default:
      return canonical;
  }
}

function defaultMax(canonical: string, display: string): number {
  switch (canonical) {
    case '°C':
      return display === '°F' ? 212 : 100;
    case 'RPM':
      return 3000;
    case 'W':
      return 300;
    case 'MHz':
      return 5000;
    case 'ms':
      return 200;
    case 'fps':
      return 165;
    case 'B/s':
      return convertUnit(125_000_000, 'B/s', display);
    default:
      return 100;
  }
}

export interface ResolvedRange {
  /** Unit of the mapping and labels. */
  unit: string;
  canonical: string;
  /** Range the sensor suggests, converted to `unit` (used as placeholders). */
  suggestedMin: number;
  suggestedMax: number;
  /** Effective range. */
  min: number;
  max: number;
}

/** Effective unit and range of a binding (BindingResolver.Resolve). */
export function resolveRange(binding: ValueBinding, sensor: SensorDto | undefined, general: GeneralSettings | undefined): ResolvedRange {
  const canonical = sensor?.unit ?? '%';
  const unit = resolveDisplayUnit(binding, sensor, general);
  const suggestedMin = sensor?.min !== undefined && sensor.min !== null ? convertUnit(sensor.min, canonical, unit) : 0;
  const suggestedMax = sensor?.max !== undefined && sensor.max !== null ? convertUnit(sensor.max, canonical, unit) : defaultMax(canonical, unit);
  const min = binding.min ?? suggestedMin;
  let max = binding.max ?? suggestedMax;
  if (Math.abs(max - min) < 1e-12) max = min + 1;
  return { unit, canonical, suggestedMin, suggestedMax, min, max };
}

/** Rounds a number for placeholders and defaults (keeps 3 significant decimals at most). */
export function tidy(value: number): number {
  if (!Number.isFinite(value)) return 0;
  const abs = Math.abs(value);
  const decimals = abs >= 100 ? 0 : abs >= 10 ? 1 : abs >= 1 ? 2 : 3;
  return Number(value.toFixed(decimals));
}
