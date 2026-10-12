import type { LiveValue, SensorCategory, SensorDto } from '@/api/types';

export type GroupMode = 'device' | 'category';

export const CATEGORY_ORDER: readonly SensorCategory[] = [
  'cpu', 'gpu', 'memory', 'storage', 'network', 'cooling', 'power', 'battery', 'motherboard',
  'audio', 'media', 'game', 'process', 'time', 'weather', 'finance', 'home', 'system', 'custom',
];

export const GROUP_HEIGHT = 40;
export const ROW_HEIGHT = 56;

export function normalize(text: string): string {
  return text.toLocaleLowerCase().normalize('NFD').replace(/\p{M}/gu, '');
}

export interface SensorEntry {
  sensor: SensorDto;
  provider: string;
  /** Normalised text the search box looks through. */
  search: string;
}

export interface GroupItem {
  type: 'group';
  key: string;
  label: string;
  count: number;
  collapsed: boolean;
}

export interface SensorItem {
  type: 'sensor';
  entry: SensorEntry;
  /** Second line of the name cell: whatever the grouping does not already say. */
  subtitle: string;
}

export type ListItem = GroupItem | SensorItem;

export interface Labels {
  category: (category: string) => string;
  kind: (kind: string | undefined) => string;
  provider: (providerId: string) => string;
}

export function buildEntries(sensors: SensorDto[], labels: Labels): SensorEntry[] {
  return sensors.map((sensor) => {
    const provider = labels.provider(sensor.providerId);
    return {
      sensor,
      provider,
      search: normalize(
        [sensor.name, sensor.id, sensor.device, sensor.host, sensor.kind, labels.kind(sensor.kind), sensor.unit, labels.category(sensor.category), provider].filter(Boolean).join(' '),
      ),
    };
  });
}

export interface Filters {
  query: string;
  categories: ReadonlySet<string>;
  hideStale: boolean;
  provider: string | null;
}

/** Everything but the category chips: those show how many sensors each category would give. */
export function filterBase(entries: SensorEntry[], { query, hideStale, provider }: Filters): SensorEntry[] {
  const terms = normalize(query).split(/\s+/).filter(Boolean);
  return entries.filter(
    ({ sensor, search }) =>
      (!provider || sensor.providerId === provider) && (!hideStale || !sensor.stale) && terms.every((term) => search.includes(term)),
  );
}

export function buildItems(entries: SensorEntry[], mode: GroupMode, labels: Labels, collapsed: ReadonlySet<string>, locale: string, expandAll: boolean): ListItem[] {
  const collator = new Intl.Collator(locale, { numeric: true, sensitivity: 'base' });
  const groups = new Map<string, { label: string; order: number; remote: boolean; entries: SensorEntry[] }>();

  for (const entry of entries) {
    const { sensor } = entry;
    let key: string;
    let label: string;
    let order = 0;
    if (mode === 'category') {
      key = sensor.category;
      label = labels.category(sensor.category);
      order = CATEGORY_ORDER.indexOf(sensor.category);
    } else {
      // Same naming as the sensor picker, so a group looks the same in both places.
      label = [sensor.host, sensor.device ?? labels.category(sensor.category)].filter(Boolean).join(' · ');
      key = `${sensor.host ?? ''}\u0000${sensor.device ?? `category:${sensor.category}`}`;
    }
    let group = groups.get(key);
    if (!group) groups.set(key, (group = { label, order, remote: !!sensor.host, entries: [] }));
    group.entries.push(entry);
  }

  const ordered = [...groups.entries()].sort(([, a], [, b]) =>
    mode === 'category' ? a.order - b.order : Number(a.remote) - Number(b.remote) || collator.compare(a.label, b.label),
  );

  const items: ListItem[] = [];
  for (const [key, group] of ordered) {
    const isCollapsed = !expandAll && collapsed.has(key);
    items.push({ type: 'group', key, label: group.label, count: group.entries.length, collapsed: isCollapsed });
    if (isCollapsed) continue;
    group.entries.sort((a, b) => collator.compare(a.sensor.name, b.sensor.name) || collator.compare(a.sensor.id, b.sensor.id));
    for (const entry of group.entries) {
      const { sensor } = entry;
      items.push({
        type: 'sensor',
        entry,
        subtitle: mode === 'category' ? [sensor.host, sensor.device].filter(Boolean).join(' · ') : labels.category(sensor.category),
      });
    }
  }
  return items;
}

/** The newest knowledge about a sensor: the live push if it is newer than the fetched snapshot. */
export function resolveValue(sensor: SensorDto, live: LiveValue | undefined) {
  const liveIsNewer = !!live?.u && (!sensor.updated || Date.parse(live.u) > Date.parse(sensor.updated));
  if (live && liveIsNewer) {
    return { value: live.v ?? null, text: live.t ?? null, stale: live.s, updated: live.u ?? undefined };
  }
  return { value: sensor.value ?? null, text: sensor.text ?? null, stale: sensor.stale, updated: sensor.updated };
}
