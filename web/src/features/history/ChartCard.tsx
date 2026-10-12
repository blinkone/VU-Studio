import { useMemo } from 'react';
import { Card, Group, Loader, Text } from '@mantine/core';
import { useElementSize, useMediaQuery } from '@mantine/hooks';
import { useTranslation } from 'react-i18next';
import { formatValue as formatWithUnit } from '@/lib/format';
import { DataTable } from './DataTable';
import { downsample, RANGE_MS, type RangeKey, type SeriesData } from './series';
import { TimeSeriesChart, type ChartSeries } from './TimeSeriesChart';
import classes from './history.module.css';

export interface ChartEntry {
  id: string;
  name: string;
  color: string;
  data: SeriesData | undefined;
  /** First load still running. */
  pending: boolean;
  error: boolean;
}

interface ChartCardProps {
  unit: string;
  entries: ChartEntry[];
  range: RangeKey;
  from: number;
  to: number;
  view: 'chart' | 'table';
  locale: string;
  fetching: boolean;
}

/** One chart per unit: every sensor in the card shares the same value axis. */
export function ChartCard({ unit, entries, range, from, to, view, locale, fetching }: ChartCardProps) {
  const { t } = useTranslation('history');
  const { ref, width } = useElementSize();
  const phone = useMediaQuery('(max-width: 48em)');

  const format = useMemo(() => (value: number) => formatWithUnit(value, unit, locale), [unit, locale]);

  const { series, stepMs } = useMemo(() => {
    const target = Math.min(600, Math.max(120, Math.round(width / 2)));
    let step = 1000;
    const list: ChartSeries[] = [];
    for (const entry of entries) {
      if (!entry.data || entry.data.points.length === 0) continue;
      const reduced = downsample(entry.data.points, RANGE_MS[range], target, entry.data.resolutionMs);
      step = Math.max(step, reduced.stepMs);
      list.push({ id: entry.id, name: entry.name, color: entry.color, points: reduced.points, band: entry.data.resolutionMs >= 60_000 });
    }
    return { series: list, stepMs: step };
  }, [entries, range, width]);

  const anyPending = entries.some((e) => e.pending);
  const allFailed = entries.every((e) => e.error);
  const title = entries.length === 1 ? entries[0].name : unit ? t('chart.titleUnit', { unit }) : t('chart.titleNoUnit');

  const empty = anyPending ? (
    <Group gap="xs">
      <Loader size={16} color="gray" />
      <Text size="sm" c="dimmed">
        {t('chart.loading')}
      </Text>
    </Group>
  ) : (
    <div style={{ maxWidth: 420 }}>
      <Text fw={600} size="sm">
        {allFailed ? t('chart.errorTitle') : t('chart.emptyTitle')}
      </Text>
      <Text size="sm" c="dimmed">
        {allFailed ? t('chart.errorBody') : t('chart.emptyBody')}
      </Text>
    </div>
  );

  return (
    <Card padding="md" component="figure" m={0}>
      <Group justify="space-between" align="flex-start" gap="md" mb="xs">
        <Text component="figcaption" fw={600} style={{ minWidth: 0 }} truncate>
          {title}
        </Text>
        {entries.length > 1 && (
          <Group gap="md" aria-label={t('chart.legend')} role="list">
            {entries.map((entry) => (
              <Group key={entry.id} gap={6} wrap="nowrap" role="listitem" style={{ minWidth: 0 }}>
                <span className={classes.keyLine} style={{ background: entry.color }} aria-hidden />
                <Text size="sm" truncate maw={200} title={entry.name}>
                  {entry.name}
                </Text>
              </Group>
            ))}
          </Group>
        )}
      </Group>

      <div ref={ref}>
        {view === 'chart' || series.length === 0 ? (
          <TimeSeriesChart
            series={series}
            from={from}
            to={to}
            unit={unit}
            stepMs={stepMs}
            height={phone ? 230 : 300}
            locale={locale}
            loading={fetching && series.length > 0}
            ariaLabel={`${title}, ${t(`ranges.${range}.long`)}`}
            empty={empty}
            formatValue={format}
          />
        ) : (
          <DataTable series={series} stepMs={stepMs} locale={locale} formatValue={format} />
        )}
      </div>
    </Card>
  );
}
