import type { DayOfWeek, DialAssignment, Profile, ProfileCondition } from '@/api/types';
import { formatValue } from '@/lib/format';
import { newId } from '@/state/configDraft';
import { toHm } from '@/features/settings/time';

// Pure helpers for profiles. They mirror the engine's rules (ProfileSelector / DialEngine in
// VuStudio.Core) so that what the UI says matches what the dials do.

export type Translate = (key: string, options?: Record<string, unknown>) => string;

/** Sensor that reports the executable name of the foreground window (published by the tray app). */
export const FOREGROUND_SENSOR = 'session/foreground/process';

export const DAYS: readonly DayOfWeek[] = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'];
export const WEEKDAYS: readonly DayOfWeek[] = DAYS.slice(0, 5);
export const WEEKEND: readonly DayOfWeek[] = DAYS.slice(5);

export type ConditionType = ProfileCondition['type'];
export const CONDITION_TYPES: readonly ConditionType[] = ['processRunning', 'foregroundApp', 'schedule', 'sensor'];

// ---------------------------------------------------------------------------- time & days

/** Localized weekday name; 2024-01-01 is a Monday, so the index in DAYS maps straight to a date. */
export function weekdayName(day: DayOfWeek, locale: string, style: 'short' | 'long' = 'short'): string {
  const date = new Date(2024, 0, 1 + DAYS.indexOf(day));
  const name = new Intl.DateTimeFormat(locale, { weekday: style }).format(date).replace(/\.$/, '');
  return name.charAt(0).toLocaleUpperCase(locale) + name.slice(1);
}

/** "Mon–Fri", "Sat, Sun", "Mon, Wed, Fri" … or `everyDay` when all seven days are selected. */
export function describeDays(days: readonly DayOfWeek[], locale: string, everyDay: string): string {
  const selected = DAYS.map((d, i) => (days.includes(d) ? i : -1)).filter((i) => i >= 0);
  if (selected.length === 7) return everyDay;
  const parts: string[] = [];
  for (let i = 0; i < selected.length; ) {
    let j = i;
    while (j + 1 < selected.length && selected[j + 1] === selected[j] + 1) j++;
    const from = weekdayName(DAYS[selected[i]], locale);
    const to = weekdayName(DAYS[selected[j]], locale);
    if (j - i >= 2) parts.push(`${from}–${to}`);
    else for (let k = i; k <= j; k++) parts.push(weekdayName(DAYS[selected[k]], locale));
    i = j + 1;
  }
  return parts.join(', ');
}

// ---------------------------------------------------------------------------- ordering

/** Profiles from highest to lowest priority; ties keep their list order (the engine's tie-break). */
export function sortProfiles<T extends Pick<Profile, 'priority'>>(profiles: readonly T[]): T[] {
  return profiles
    .map((profile, index) => ({ profile, index }))
    .sort((a, b) => b.profile.priority - a.profile.priority || a.index - b.index)
    .map((entry) => entry.profile);
}

export function nextPriority(profiles: readonly Pick<Profile, 'priority'>[]): number {
  return profiles.reduce((max, p) => Math.max(max, p.priority), -1) + 1;
}

/**
 * Moves a profile one place up or down in the priority order by exchanging priority numbers.
 * Works on a draft array. When several profiles share a number the order is ambiguous, so the
 * whole list is renumbered instead.
 */
export function moveProfile(profiles: Profile[], id: string, direction: 'up' | 'down'): void {
  const order = sortProfiles(profiles);
  const from = order.findIndex((p) => p.id === id);
  const to = direction === 'up' ? from - 1 : from + 1;
  if (from < 0 || to < 0 || to >= order.length) return;

  [order[from], order[to]] = [order[to], order[from]];
  const numbers = profiles.map((p) => p.priority).sort((a, b) => b - a);
  const distinct = new Set(numbers).size === numbers.length;
  order.forEach((profile, rank) => {
    profile.priority = distinct ? numbers[rank] : order.length - 1 - rank;
  });
}

/** The profile unassigned dials inherit from: first enabled "always" profile, else the first one. */
export function findDefaultProfile(profiles: readonly Profile[]): Profile | undefined {
  return profiles.find((p) => p.enabled && p.activation === 'always') ?? profiles[0];
}

/**
 * An "always" profile with a higher priority wins every time, so an automatic profile below it can
 * never activate. Returns that profile, if any.
 */
export function findShadowingProfile(profile: Profile, profiles: readonly Profile[]): Profile | undefined {
  if (!profile.enabled || profile.activation !== 'auto') return undefined;
  const index = profiles.indexOf(profile);
  return profiles.find(
    (other, i) =>
      other !== profile &&
      other.enabled &&
      other.activation === 'always' &&
      (other.priority > profile.priority || (other.priority === profile.priority && i < index)),
  );
}

// ---------------------------------------------------------------------------- creating

export function uniqueId(taken: Iterable<string>): string {
  const used = new Set(taken);
  let id = newId();
  while (used.has(id)) id = newId();
  return id;
}

export function blankProfile(profiles: readonly Profile[], name: string): Profile {
  return {
    id: uniqueId(profiles.map((p) => p.id)),
    name,
    enabled: true,
    priority: nextPriority(profiles),
    activation: 'auto',
    match: 'any',
    conditions: [],
    inheritUnassigned: true,
    dials: [],
  };
}

/** "Name (copy)", "Name (copy 2)" … the first name not used yet. */
export function copyName(name: string, existing: readonly string[], suffix: string): string {
  const taken = new Set(existing.map((n) => n.toLocaleLowerCase()));
  let candidate = `${name} (${suffix})`;
  for (let n = 2; taken.has(candidate.toLocaleLowerCase()); n++) candidate = `${name} (${suffix} ${n})`;
  return candidate;
}

/**
 * Deep copy with fresh ids: a new profile id and new page ids. Layer ids are only unique within
 * one face document, so they are kept. A copy of an "always" profile becomes "automatic" so that it
 * does not silently outrank everything else.
 */
export function duplicateProfile(source: Profile, profiles: readonly Profile[], name: string): Profile {
  const copy = structuredClone(source);
  copy.id = uniqueId(profiles.map((p) => p.id));
  copy.name = name;
  copy.priority = nextPriority(profiles);
  if (copy.activation === 'always') copy.activation = 'auto';
  for (const assignment of copy.dials) {
    const used = new Set<string>();
    for (const page of assignment.pages) {
      page.id = uniqueId(used);
      used.add(page.id);
    }
  }
  return copy;
}

export function newCondition(type: ConditionType): ProfileCondition {
  switch (type) {
    case 'processRunning':
      return { type, processes: [] };
    case 'foregroundApp':
      return { type, processes: [] };
    case 'schedule':
      return { type, days: [...WEEKDAYS], from: '09:00:00', to: '18:00:00' };
    case 'sensor':
      return { type, sensorId: '', comparison: 'above', value: 80, forSeconds: 10 };
  }
}

// ---------------------------------------------------------------------------- summaries

interface SummaryContext {
  t: Translate;
  locale: string;
  sensorName: (id: string) => string | undefined;
  sensorUnit: (id: string) => string;
}

export interface ConditionSummary {
  type: ConditionType;
  text: string;
  /** The condition cannot match as configured (nothing selected, no sensor …). */
  incomplete: boolean;
}

function listNames(names: readonly string[], max = 3): string {
  return names.length <= max ? names.join(', ') : `${names.slice(0, max).join(', ')} +${names.length - max}`;
}

export function isScheduleEmpty(condition: Extract<ProfileCondition, { type: 'schedule' }>): boolean {
  return condition.days.length === 0 || toHm(condition.from) === toHm(condition.to);
}

export function summarizeCondition(condition: ProfileCondition, ctx: SummaryContext): ConditionSummary {
  const { t, locale } = ctx;
  switch (condition.type) {
    case 'processRunning':
    case 'foregroundApp': {
      const names = condition.processes.map((p) => p.trim()).filter(Boolean);
      return {
        type: condition.type,
        incomplete: names.length === 0,
        text: names.length === 0 ? t('summary.noApps') : t(`summary.${condition.type}`, { names: listNames(names) }),
      };
    }
    case 'schedule':
      return {
        type: condition.type,
        incomplete: isScheduleEmpty(condition),
        text: t('summary.schedule', {
          days: condition.days.length === 0 ? t('summary.noDays') : describeDays(condition.days, locale, t('summary.everyDay')),
          from: toHm(condition.from),
          to: toHm(condition.to),
        }),
      };
    case 'sensor': {
      if (!condition.sensorId) return { type: condition.type, incomplete: true, text: t('summary.noSensor') };
      const name = ctx.sensorName(condition.sensorId) ?? condition.sensorId;
      return {
        type: condition.type,
        incomplete: false,
        text: t(condition.forSeconds > 0 ? 'summary.sensorFor' : 'summary.sensor', {
          sensor: name,
          comparison: t(`comparison.${condition.comparison}`),
          value: formatValue(condition.value, ctx.sensorUnit(condition.sensorId), locale),
          seconds: condition.forSeconds,
        }),
      };
    }
  }
}

/** True when an assignment actually shows something (the engine treats empty assignments as unassigned). */
export function hasPages(assignment: DialAssignment | undefined): assignment is DialAssignment {
  return !!assignment && assignment.pages.length > 0;
}
