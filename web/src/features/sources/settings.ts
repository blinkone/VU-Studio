import type { SettingField, SettingKind } from '@/api/types';

export type SettingsValues = Record<string, unknown>;

/** Forms with more fields than this are split into a main part and a collapsible "more options" part. */
export const MAIN_FIELDS_LIMIT = 6;

/** Deep copy of the defaults declared by a provider type; this is what a new instance starts with. */
export function defaultSettings(fields: SettingField[]): SettingsValues {
  const values: SettingsValues = {};
  for (const field of fields) {
    if (field.default !== undefined && field.default !== null) {
      values[field.key] = structuredClone(field.default);
    }
  }
  return values;
}

/** Removes `undefined` entries (cleared number inputs) so they are not stored, also inside table rows. */
export function pruneSettings<T>(value: T): T {
  if (Array.isArray(value)) {
    return value.map((item) => pruneSettings(item)) as T;
  }
  if (value !== null && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, item]) => item !== undefined)
      .map(([key, item]) => [key, pruneSettings(item)] as const);
    return Object.fromEntries(entries) as T;
  }
  return value;
}

/**
 * The settings to store: defaults for everything the user left out, then the user's values.
 * The service validates "required" against the stored JSON only, so defaults must be written.
 */
export function materializeSettings(fields: SettingField[], values: SettingsValues): SettingsValues {
  return { ...defaultSettings(fields), ...pruneSettings(values) };
}

function isFilled(field: SettingField, raw: unknown): boolean {
  const value = raw ?? field.default;
  if (value === undefined || value === null) return false;
  if (typeof value === 'string') return value.trim() !== '';
  if (Array.isArray(value)) return value.length > 0;
  return true;
}

/** Required fields that are still empty (what the service would reject on save). */
export function missingRequired(fields: SettingField[], values: SettingsValues): SettingField[] {
  return fields.filter((field) => field.required && !isFilled(field, values[field.key]));
}

const TUNING_KINDS: ReadonlySet<SettingKind> = new Set<SettingKind>(['number', 'seconds', 'boolean', 'select']);

/** Optional knobs that already have a sensible default: poll intervals, switches, modes… */
function isTuning(field: SettingField): boolean {
  return !field.required && field.default !== undefined && field.default !== null && TUNING_KINDS.has(field.kind);
}

/**
 * Splits a long settings schema so the form stays usable: what identifies the source (addresses,
 * credentials, lists, anything required) first, tuning knobs with defaults behind "more options".
 * Short schemas are returned untouched.
 */
export function splitFields(fields: SettingField[]): { main: SettingField[]; more: SettingField[] } {
  if (fields.length <= MAIN_FIELDS_LIMIT) return { main: fields, more: [] };

  const main = fields.filter((field) => !isTuning(field));
  const more = fields.filter(isTuning);
  if (main.length === 0 || more.length === 0) {
    // Nothing tells the two apart: keep the schema's own order, the author listed the important ones first.
    return { main: fields.slice(0, MAIN_FIELDS_LIMIT), more: fields.slice(MAIN_FIELDS_LIMIT) };
  }
  return { main, more };
}

function canonical(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonical);
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
        .map(([key, item]) => [key, canonical(item)] as const),
    );
  }
  return value;
}

/** Structural equality for plain JSON values, independent of key order (is an instance still as saved?). */
export function sameJson(a: unknown, b: unknown): boolean {
  return JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));
}
