import { useMemo } from 'react';
import { Table } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { makeTimeFormatters } from './scale';
import type { ChartSeries } from './TimeSeriesChart';
import classes from './history.module.css';

interface DataTableProps {
  series: ChartSeries[];
  stepMs: number;
  locale: string;
  formatValue: (value: number) => string;
}

/** The chart's data as a table: the accessible twin of every chart. Newest rows first. */
export function DataTable({ series, stepMs, locale, formatValue }: DataTableProps) {
  const { t } = useTranslation('history');
  const time = useMemo(() => makeTimeFormatters(locale), [locale]);

  const rows = useMemo(() => {
    const byTime = new Map<number, Map<string, ChartSeries['points'][number]>>();
    for (const s of series) {
      for (const p of s.points) {
        let row = byTime.get(p.t);
        if (!row) byTime.set(p.t, (row = new Map()));
        row.set(s.id, p);
      }
    }
    return [...byTime.entries()].sort((a, b) => b[0] - a[0]);
  }, [series]);

  if (rows.length === 0) return null;

  return (
    <div className={classes.tableWrap} tabIndex={0} role="region" aria-label={t('table.label')}>
      <Table className={classes.table} stickyHeader verticalSpacing={6} horizontalSpacing="md" striped="even">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('table.time')}</Table.Th>
            {series.map((s) => (
              <Table.Th key={s.id}>{s.name}</Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map(([timestamp, cells]) => (
            <Table.Tr key={timestamp}>
              <Table.Td>{time.full(timestamp, stepMs < 60_000)}</Table.Td>
              {series.map((s) => {
                const p = cells.get(s.id);
                return (
                  <Table.Td key={s.id}>
                    {p ? formatValue(p.avg) : '–'}
                    {p && s.band && p.max > p.min && (
                      <span className={classes.range}>
                        {' '}
                        ({formatValue(p.min)} – {formatValue(p.max)})
                      </span>
                    )}
                  </Table.Td>
                );
              })}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </div>
  );
}
