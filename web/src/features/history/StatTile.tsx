import { Card, Group, Loader, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { makeTimeFormatters } from './scale';
import type { SeriesStats } from './series';
import classes from './history.module.css';

interface StatTileProps {
  name: string;
  /** Device or host, shown under the name. */
  context?: string;
  /** CSS color of the series (a `var(--series-N)` token). */
  color: string;
  stats: SeriesStats | null;
  loading: boolean;
  /** The chart's left edge and the data step, to tell when recording started mid-range. */
  rangeStart: number;
  stepMs: number;
  locale: string;
  formatValue: (value: number) => string;
}

/** Min / avg / max / last of one sensor over the selected range. */
export function StatTile({ name, context, color, stats, loading, rangeStart, stepMs, locale, formatValue }: StatTileProps) {
  const { t } = useTranslation('history');
  const startedLate = stats !== null && stats.firstT - rangeStart > Math.max(stepMs * 3, (Date.now() - rangeStart) * 0.08);

  const cells: { key: 'min' | 'avg' | 'max' | 'last'; value: number | undefined }[] = [
    { key: 'min', value: stats?.min },
    { key: 'avg', value: stats?.avg },
    { key: 'max', value: stats?.max },
    { key: 'last', value: stats?.last },
  ];

  return (
    <Card padding="md" className={classes.tile} component="section" aria-label={name}>
      <Group gap="xs" wrap="nowrap" align="center">
        <span className={classes.keyLine} style={{ background: color }} aria-hidden />
        <div style={{ minWidth: 0, flex: 1 }}>
          <Text fw={600} truncate title={name}>
            {name}
          </Text>
          {context && (
            <Text size="xs" c="dimmed" truncate>
              {context}
            </Text>
          )}
        </div>
      </Group>

      {stats ? (
        <dl className={classes.stats} style={{ marginTop: 12 }}>
          {cells.map(({ key, value }) => (
            <div key={key} className={classes.stat} data-main={key === 'last' || undefined}>
              <dt>{t(`stats.${key}`)}</dt>
              <dd title={value === undefined ? undefined : formatValue(value)}>{value === undefined ? '–' : formatValue(value)}</dd>
            </div>
          ))}
        </dl>
      ) : (
        <Group gap="xs" mt="md" c="dimmed">
          {loading && <Loader size={14} color="gray" />}
          <Text size="sm" c="dimmed">
            {loading ? t('stats.loading') : t('stats.noData')}
          </Text>
        </Group>
      )}

      {startedLate && stats && (
        <Text size="xs" c="dimmed" mt="sm">
          {t('stats.since', { time: makeTimeFormatters(locale).dateTime(stats.firstT) })}
        </Text>
      )}
    </Card>
  );
}
