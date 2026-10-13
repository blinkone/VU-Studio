import { memo, useMemo, type KeyboardEvent, type ReactNode } from 'react';
import { Highlight, Text } from '@mantine/core';
import { IconChevronRight, IconClockPause } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { useLive } from '@/api/live';
import { MdiIcon } from '@/components/MdiIcon';
import { formatValue } from '@/lib/format';
import { GROUP_HEIGHT, resolveValue, ROW_HEIGHT, type ListItem, type SensorItem } from './model';
import { useWindowVirtualizer } from './useWindowVirtualizer';
import classes from './sensors.module.css';

const sizeOf = (items: ListItem[]) => (index: number) => (items[index].type === 'group' ? GROUP_HEIGHT : ROW_HEIGHT);

interface SensorTableProps {
  items: ListItem[];
  locale: string;
  highlight: string[];
  onOpen: (sensorId: string) => void;
  onToggleGroup: (key: string) => void;
}

/** Virtualised list of sensors and group headers laid out as a table. */
export function SensorTable({ items, locale, highlight, onOpen, onToggleGroup }: SensorTableProps) {
  const { t } = useTranslation('sensors');
  const size = useMemo(() => sizeOf(items), [items]);
  const { containerRef, total, offsets, start, end } = useWindowVirtualizer(items.length, size);

  return (
    <div className={classes.table} role="table" aria-label={t('table.label')} aria-rowcount={items.length + 1}>
      <div className={classes.head} role="row" aria-rowindex={1}>
        <div role="columnheader">{t('columns.name')}</div>
        <div role="columnheader" style={{ textAlign: 'right' }}>
          {t('columns.value')}
        </div>
        <div role="columnheader" className={classes.cUnit}>
          {t('columns.unit')}
        </div>
        <div role="columnheader" className={classes.cKind}>
          {t('columns.kind')}
        </div>
        <div role="columnheader" className={classes.cRange}>
          {t('columns.range')}
        </div>
        <div role="columnheader" className={classes.cProvider}>
          {t('columns.provider')}
        </div>
        <div role="columnheader">
          <span className={classes.stateText}>{t('columns.state')}</span>
        </div>
      </div>

      <div ref={containerRef} className={classes.list} style={{ height: total }} role="rowgroup">
        {items.slice(start, end).map((item, i) => {
          const index = start + i;
          const style = { top: offsets[index], height: size(index) };
          if (item.type === 'group') {
            return (
              <div key={`g:${item.key}`} className={classes.groupRow} style={style} role="row" aria-rowindex={index + 2}>
                <button type="button" className={classes.group} role="rowheader" aria-expanded={!item.collapsed} onClick={() => onToggleGroup(item.key)}>
                  <IconChevronRight size={16} className={classes.chevron} data-open={!item.collapsed} aria-hidden />
                  <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{item.label}</span>
                  <Text span size="xs" c="dimmed" fw={500}>
                    {item.count}
                  </Text>
                </button>
              </div>
            );
          }
          return <SensorRow key={`s:${item.entry.sensor.id}`} item={item} style={style} rowIndex={index + 2} locale={locale} highlight={highlight} onOpen={onOpen} />;
        })}
      </div>
    </div>
  );
}

interface SensorRowProps {
  item: SensorItem;
  style: { top: number; height: number };
  rowIndex: number;
  locale: string;
  highlight: string[];
  onOpen: (sensorId: string) => void;
}

const SensorRow = memo(function SensorRow({ item, style, rowIndex, locale, highlight, onOpen }: SensorRowProps) {
  const { t } = useTranslation('sensors');
  const { sensor, provider } = item.entry;
  // Only this row re-renders when its own value arrives: unchanged entries keep their identity in the store.
  const live = useLive((s) => s.values[sensor.id]);
  const now = resolveValue(sensor, live);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onOpen(sensor.id);
    }
  };

  const range =
    sensor.valueKind === 'number' && (sensor.min !== undefined || sensor.max !== undefined)
      ? `${sensor.min === undefined ? '…' : formatValue(sensor.min, sensor.unit, locale)} – ${sensor.max === undefined ? '…' : formatValue(sensor.max, sensor.unit, locale)}`
      : '–';
  const kind = sensor.kind ? t(`kinds.${sensor.kind}`, { defaultValue: sensor.kind }) : '–';

  let value: ReactNode;
  if (sensor.valueKind === 'number') value = formatValue(now.value, sensor.unit, locale);
  else if (sensor.valueKind === 'color' && now.text) value = <><span className={classes.swatch} style={{ background: now.text }} />{now.text}</>;
  else value = now.text ?? '–';

  return (
    <div className={classes.row} style={style} role="row" aria-rowindex={rowIndex} tabIndex={0} onClick={() => onOpen(sensor.id)} onKeyDown={onKeyDown}>
      <div role="cell" style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
        <span style={{ flex: 'none', display: 'grid', placeItems: 'center', width: 20, color: 'var(--mantine-color-dimmed)' }}>
          <MdiIcon id={sensor.icon} size={18} />
        </span>
        <div style={{ minWidth: 0 }}>
          <Highlight highlight={highlight} fw={550} size="sm" truncate style={{ lineHeight: 1.35 }}>
            {sensor.name}
          </Highlight>
          <Text size="xs" c="dimmed" truncate>
            {item.subtitle || sensor.id}
          </Text>
        </div>
      </div>
      <div role="cell" className={classes.value} data-stale={now.stale || undefined} style={{ opacity: now.stale ? 0.6 : 1 }} title={typeof value === 'string' ? value : undefined}>
        {value}
      </div>
      <div role="cell" className={`${classes.cUnit} ${classes.num}`}>
        <Text size="sm" c="dimmed" span>
          {sensor.unit || '–'}
        </Text>
      </div>
      <div role="cell" className={classes.cKind}>
        <Text size="sm" truncate>
          {kind}
        </Text>
      </div>
      <div role="cell" className={classes.cRange}>
        <Text size="xs" c="dimmed" truncate className={classes.num}>
          {range}
        </Text>
      </div>
      <div role="cell" className={classes.cProvider}>
        <Text size="sm" truncate title={sensor.providerId}>
          {provider}
        </Text>
      </div>
      <div role="cell" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        {now.stale ? (
          <IconClockPause size={16} color="var(--mantine-color-yellow-6)" aria-hidden />
        ) : (
          <span className={classes.dot} aria-hidden />
        )}
        <Text size="xs" c="dimmed" className={classes.stateText}>
          {now.stale ? t('state.stale') : t('state.live')}
        </Text>
        <span style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clipPath: 'inset(50%)' }}>{now.stale ? t('state.stale') : t('state.live')}</span>
      </div>
    </div>
  );
});
