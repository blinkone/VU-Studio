import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ActionIcon, Button, Group, Loader, Menu, SegmentedControl, SimpleGrid, Stack, Text, Title, Tooltip } from '@mantine/core';
import { useQueries } from '@tanstack/react-query';
import { IconAlertTriangle, IconChartLine, IconDownload, IconX } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { useSensors } from '@/api/queries';
import type { SensorDto } from '@/api/types';
import { SensorPicker } from '@/components/SensorPicker';
import { formatValue } from '@/lib/format';
import { ChartCard, type ChartEntry } from './ChartCard';
import { makeTimeFormatters } from './scale';
import { buildCsv, computeStats, downloadCsv, fetchSeries, parseRange, RANGE_MS, RANGES, REFRESH_MS, type RangeKey } from './series';
import { StatTile } from './StatTile';
import chart from './chart.module.css';
import classes from './history.module.css';

const MAX_SENSORS = 4;
const STORAGE_KEY = 'vustudio.history';
const SUGGESTED_CATEGORIES = ['cpu', 'gpu', 'memory', 'network', 'cooling', 'power'];

interface Saved {
  ids: string[];
  range: RangeKey;
}

function readSaved(): Saved | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const value = JSON.parse(raw) as Partial<Saved>;
    return { ids: Array.isArray(value.ids) ? value.ids.filter((id): id is string => typeof id === 'string').slice(0, MAX_SENSORS) : [], range: parseRange(value.range ?? null) };
  } catch {
    return null;
  }
}

function writeSaved(saved: Saved) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(saved));
  } catch {
    // Private mode or blocked storage: the URL still carries the selection.
  }
}

const isNumeric = (sensor: SensorDto) => sensor.valueKind === 'number';

export function HistoryView() {
  const { t, i18n } = useTranslation('history');
  const locale = i18n.language;
  const [params, setParams] = useSearchParams();
  const { data: sensors = [] } = useSensors();
  const [view, setView] = useViewState();

  const paramKey = params.toString();
  // `params` is a new object on every navigation; its string form is the stable key.
  const ids = useMemo(() => [...new Set(new URLSearchParams(paramKey).getAll('s').filter(Boolean))].slice(0, MAX_SENSORS), [paramKey]);
  const range = parseRange(params.get('r'));

  const update = useCallback(
    (nextIds: string[], nextRange: RangeKey) => {
      setParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          next.delete('s');
          nextIds.forEach((id) => next.append('s', id));
          next.set('r', nextRange);
          return next;
        },
        { replace: true },
      );
      writeSaved({ ids: nextIds, range: nextRange });
    },
    [setParams],
  );

  // Coming back to the page without a link: reopen what the user looked at last.
  useEffect(() => {
    if (params.has('s') || params.has('r')) return;
    const saved = readSaved();
    if (saved && saved.ids.length > 0) {
      setParams(
        () => {
          const next = new URLSearchParams();
          saved.ids.forEach((id) => next.append('s', id));
          next.set('r', saved.range);
          return next;
        },
        { replace: true },
      );
    }
    // Only at mount.
  }, []);

  // A color slot belongs to a sensor for as long as it is selected: removing one never repaints the others.
  const slotsRef = useRef(new Map<string, number>());
  const slots = useMemo(() => {
    const map = slotsRef.current;
    for (const id of [...map.keys()]) if (!ids.includes(id)) map.delete(id);
    const used = new Set(map.values());
    for (const id of ids) {
      if (map.has(id)) continue;
      let slot = 0;
      while (used.has(slot)) slot++;
      map.set(id, slot);
      used.add(slot);
    }
    return map;
  }, [ids]);

  const byId = useMemo(() => new Map(sensors.map((s) => [s.id, s])), [sensors]);
  const entries = useMemo(
    () =>
      ids.map((id) => {
        const sensor = byId.get(id);
        return {
          id,
          sensor,
          name: sensor?.name ?? id,
          unit: sensor?.unit ?? '',
          color: `var(--series-${(slots.get(id) ?? 0) + 1})`,
          chartable: !sensor || isNumeric(sensor),
        };
      }),
    [ids, byId, slots],
  );
  const chartable = entries.filter((e) => e.chartable);

  const results = useQueries({
    queries: chartable.map((entry) => ({
      queryKey: ['history', entry.id, range] as const,
      queryFn: () => fetchSeries(entry.id, range),
      refetchInterval: REFRESH_MS[range],
      staleTime: 1000,
    })),
  });
  const resultOf = (id: string) => results[chartable.findIndex((e) => e.id === id)];

  const fetching = results.some((r) => r.isFetching);
  const updatedAt = Math.max(0, ...results.map((r) => r.dataUpdatedAt));
  // The right edge is the time of the latest fetch, so the window only moves when new data arrives.
  const lastFetch = Math.max(0, ...results.map((r) => r.data?.fetchedAt ?? 0));
  const openedAt = useMemo(() => Date.now(), [range, ids.length]);
  const to = lastFetch || openedAt;
  const from = to - RANGE_MS[range];

  // ----- charts: one card per unit, in the order the sensors were picked
  const units = useMemo(() => [...new Set(chartable.map((e) => e.unit))], [chartable]);

  const pickerFilter = useCallback((s: SensorDto) => isNumeric(s) && !ids.includes(s.id), [ids]);
  const suggestions = useMemo(() => {
    const picked: SensorDto[] = [];
    for (const category of SUGGESTED_CATEGORIES) {
      const match = sensors.find((s) => s.category === category && isNumeric(s) && !s.stale && s.value !== undefined);
      if (match) picked.push(match);
    }
    return picked.slice(0, 4);
  }, [sensors]);

  const add = (id: string | undefined) => {
    if (id && !ids.includes(id) && ids.length < MAX_SENSORS) update([...ids, id], range);
  };

  const exportCsv = (flavor: 'standard' | 'excel') => {
    const rows = chartable.flatMap((entry) => {
      const data = resultOf(entry.id)?.data;
      return data ? [{ id: entry.id, name: entry.name, unit: entry.unit, points: data.points }] : [];
    });
    const now = new Date();
    const stamp = `${now.getFullYear()}${String(now.getMonth() + 1).padStart(2, '0')}${String(now.getDate()).padStart(2, '0')}-${String(now.getHours()).padStart(2, '0')}${String(now.getMinutes()).padStart(2, '0')}`;
    downloadCsv(`vustudio-history-${range}-${stamp}.csv`, buildCsv(rows, { flavor }), flavor);
  };
  const hasExportableData = chartable.some((e) => (resultOf(e.id)?.data?.points.length ?? 0) > 0);

  return (
    <Stack gap="lg" pb={96} className={chart.tokens}>
      <Group justify="space-between" align="flex-start" gap="md">
        <div>
          <Title order={2}>{t('title')}</Title>
          <Text c="dimmed" maw={720}>
            {t('subtitle')}
          </Text>
        </div>
        {ids.length > 0 && (
          <Group gap="xs">
            <SegmentedControl
              size="xs"
              value={view}
              onChange={(value) => setView(value === 'table' ? 'table' : 'chart')}
              aria-label={t('view.label')}
              data={[
                { value: 'chart', label: t('view.chart') },
                { value: 'table', label: t('view.table') },
              ]}
            />
            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <Button size="xs" variant="default" leftSection={<IconDownload size={15} />} disabled={!hasExportableData}>
                  {t('export.button')}
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item onClick={() => exportCsv('standard')}>
                  <Text size="sm">{t('export.standard')}</Text>
                  <Text size="xs" c="dimmed">
                    {t('export.standardHint')}
                  </Text>
                </Menu.Item>
                <Menu.Item onClick={() => exportCsv('excel')}>
                  <Text size="sm">{t('export.excel')}</Text>
                  <Text size="xs" c="dimmed">
                    {t('export.excelHint')}
                  </Text>
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        )}
      </Group>

      {/* One row of filters above everything they scope. */}
      <Stack gap="sm">
        <Group gap="md" align="flex-end" wrap="wrap">
          <SensorPicker
            value={undefined}
            onChange={add}
            sensorFilter={pickerFilter}
            w={{ base: '100%', sm: 340 }}
            label={t('picker.label')}
            placeholder={ids.length >= MAX_SENSORS ? t('picker.full', { count: MAX_SENSORS }) : t('picker.placeholder')}
            disabled={ids.length >= MAX_SENSORS}
            clearable={false}
          />
          <SegmentedControl
            value={range}
            onChange={(value) => update(ids, parseRange(value))}
            aria-label={t('range.label')}
            data={RANGES.map((key) => ({ value: key, label: t(`ranges.${key}.short`) }))}
            fullWidth
            style={{ flex: '1 1 320px', maxWidth: 440 }}
          />
          {ids.length > 0 && (
            <Group gap={6} style={{ marginLeft: 'auto' }} c="dimmed">
              {fetching && <Loader size={12} color="gray" />}
              {updatedAt > 0 && <Text size="xs">{t('updated', { time: makeTimeFormatters(locale).time(updatedAt, true) })}</Text>}
            </Group>
          )}
        </Group>

        {entries.length > 0 && (
          <Group gap="xs" role="list" aria-label={t('chips.label')}>
            {entries.map((entry) => {
              const warning = entry.sensor === undefined ? t('chips.missing') : !entry.chartable ? t('chips.notNumeric') : null;
              return (
                <div key={entry.id} className={classes.chip} data-warn={!!warning || undefined} role="listitem">
                  <span className={classes.keyLine} style={{ background: entry.chartable ? entry.color : 'var(--mantine-color-gray-5)' }} aria-hidden />
                  <Text size="sm" fw={500} truncate maw={260} title={entry.id}>
                    {entry.name}
                  </Text>
                  {warning && (
                    <Tooltip label={warning} multiline w={240}>
                      <IconAlertTriangle size={15} color="var(--mantine-color-yellow-6)" aria-label={warning} role="img" />
                    </Tooltip>
                  )}
                  <ActionIcon
                    variant="subtle"
                    color="gray"
                    size="sm"
                    radius="xl"
                    aria-label={t('chips.remove', { name: entry.name })}
                    onClick={() => update(ids.filter((id) => id !== entry.id), range)}
                  >
                    <IconX size={14} />
                  </ActionIcon>
                </div>
              );
            })}
          </Group>
        )}
      </Stack>

      {ids.length === 0 ? (
        <EmptyHistory suggestions={suggestions} onPick={(id) => add(id)} />
      ) : (
        <>
          <SimpleGrid cols={{ base: 1, sm: 2, lg: Math.min(chartable.length, 4) || 1 }} spacing="md">
            {chartable.map((entry) => {
              const result = resultOf(entry.id);
              const data = result?.data;
              return (
                <StatTile
                  key={entry.id}
                  name={entry.name}
                  context={[entry.sensor?.host, entry.sensor?.device].filter(Boolean).join(' · ') || undefined}
                  color={entry.color}
                  stats={data ? computeStats(data.points) : null}
                  loading={!result || result.isPending}
                  rangeStart={from}
                  stepMs={data?.resolutionMs ?? 1000}
                  locale={locale}
                  formatValue={(value) => formatValue(value, entry.unit, locale)}
                />
              );
            })}
          </SimpleGrid>

          <Stack gap="md">
            {units.map((unit) => {
              const group: ChartEntry[] = chartable
                .filter((e) => e.unit === unit)
                .map((e) => {
                  const result = resultOf(e.id);
                  return { id: e.id, name: e.name, color: e.color, data: result?.data, pending: !result || result.isPending, error: !!result?.isError };
                });
              return <ChartCard key={unit || '(none)'} unit={unit} entries={group} range={range} from={from} to={to} view={view} locale={locale} fetching={fetching} />;
            })}
          </Stack>
          <Text size="xs" c="dimmed" maw={720}>
            {t('footnote')}
          </Text>
        </>
      )}
    </Stack>
  );
}

/** Chart or table: a per-viewer convenience, remembered in this browser only. */
function useViewState(): ['chart' | 'table', (value: 'chart' | 'table') => void] {
  const [view, setView] = useState<'chart' | 'table'>(() => {
    try {
      return localStorage.getItem('vustudio.history.view') === 'table' ? 'table' : 'chart';
    } catch {
      return 'chart';
    }
  });
  return [
    view,
    (value) => {
      setView(value);
      try {
        localStorage.setItem('vustudio.history.view', value);
      } catch {
        // Not remembered, still works.
      }
    },
  ];
}

interface EmptyHistoryProps {
  suggestions: SensorDto[];
  onPick: (id: string) => void;
}

function EmptyHistory({ suggestions, onPick }: EmptyHistoryProps) {
  const { t } = useTranslation('history');
  return (
    <Stack align="center" gap="sm" py={48} ta="center" style={{ border: '1px dashed var(--mantine-color-default-border)', borderRadius: 'var(--mantine-radius-lg)' }}>
      <IconChartLine size={36} stroke={1.4} color="var(--mantine-color-dimmed)" aria-hidden />
      <Text fw={650} size="lg">
        {t('empty.title')}
      </Text>
      <Text c="dimmed" maw={520} px="md">
        {t('empty.body')}
      </Text>
      {suggestions.length > 0 && (
        <>
          <Text size="xs" c="dimmed" mt="xs">
            {t('empty.suggestions')}
          </Text>
          <Group gap="xs" justify="center" px="md">
            {suggestions.map((sensor) => (
              <button key={sensor.id} type="button" className={classes.suggestion} onClick={() => onPick(sensor.id)}>
                {sensor.name}
                {sensor.device ? <Text span size="xs" c="dimmed">{sensor.device}</Text> : null}
              </button>
            ))}
          </Group>
        </>
      )}
    </Stack>
  );
}
