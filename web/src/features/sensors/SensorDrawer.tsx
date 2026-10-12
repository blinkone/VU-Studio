import { useMemo } from 'react';
import { ActionIcon, Anchor, Badge, Button, Code, CopyButton, Divider, Drawer, Group, Loader, Stack, Text, Tooltip } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { IconCheck, IconChartLine, IconCopy } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { useLive } from '@/api/live';
import type { SensorDto } from '@/api/types';
import { MdiIcon } from '@/components/MdiIcon';
import { formatValue } from '@/lib/format';
import chart from '@/features/history/chart.module.css';
import { fetchRecent } from '@/features/history/series';
import { TimeSeriesChart } from '@/features/history/TimeSeriesChart';
import { resolveValue, type Labels } from './model';
import classes from './sensors.module.css';

const WINDOW_SECONDS = 600;

interface SensorDrawerProps {
  sensor: SensorDto | undefined;
  opened: boolean;
  onClose: () => void;
  onExited: () => void;
  labels: Labels;
  locale: string;
}

export function SensorDrawer({ sensor, opened, onClose, onExited, labels, locale }: SensorDrawerProps) {
  const { t } = useTranslation('sensors');
  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      onExitTransitionEnd={onExited}
      position="right"
      size="min(560px, 100vw)"
      padding="md"
      title={
        sensor && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <MdiIcon id={sensor.icon} size={22} />
            <span style={{ fontWeight: 650 }}>{sensor.name}</span>
          </span>
        )
      }
      closeButtonProps={{ 'aria-label': t('drawer.close') }}
    >
      {sensor && <DrawerBody sensor={sensor} labels={labels} locale={locale} />}
    </Drawer>
  );
}

function DrawerBody({ sensor, labels, locale }: { sensor: SensorDto; labels: Labels; locale: string }) {
  const { t } = useTranslation('sensors');
  const navigate = useNavigate();
  const live = useLive((s) => s.values[sensor.id]);
  const now = resolveValue(sensor, live);
  const numeric = sensor.valueKind === 'number';

  const recent = useQuery({
    queryKey: ['history', 'recent', sensor.id, WINDOW_SECONDS],
    queryFn: () => fetchRecent(sensor.id, WINDOW_SECONDS),
    refetchInterval: 5_000,
    enabled: numeric,
  });
  const format = useMemo(() => (value: number) => formatValue(value, sensor.unit, locale), [sensor.unit, locale]);
  const series = useMemo(
    () => (recent.data && recent.data.points.length > 0 ? [{ id: sensor.id, name: sensor.name, color: 'var(--series-1)', points: recent.data.points, band: false }] : []),
    [recent.data, sensor.id, sensor.name],
  );
  const to = recent.data?.fetchedAt ?? Date.now();

  const placeholder = `{sensor:${sensor.id}}`;
  const updated = now.updated ? new Date(now.updated).toLocaleString(locale) : '–';
  const range =
    sensor.min !== undefined || sensor.max !== undefined
      ? `${sensor.min === undefined ? '…' : formatValue(sensor.min, sensor.unit, locale)} – ${sensor.max === undefined ? '…' : formatValue(sensor.max, sensor.unit, locale)}`
      : '–';

  const facts: [string, React.ReactNode][] = [
    [t('facts.device'), [sensor.host, sensor.device].filter(Boolean).join(' · ') || '–'],
    [t('facts.category'), labels.category(sensor.category)],
    [t('facts.kind'), sensor.kind ? labels.kind(sensor.kind) : '–'],
    [t('facts.unit'), sensor.unit || '–'],
    [t('facts.valueKind'), t(`valueKinds.${sensor.valueKind}`)],
    [t('facts.range'), numeric ? range : '–'],
    [t('facts.provider'), <Anchor key="p" component={Link} to={`/sources?edit=${encodeURIComponent(sensor.providerId)}`} size="sm">{labels.provider(sensor.providerId)}</Anchor>],
    [t('facts.updated'), updated],
  ];

  return (
    <Stack gap="lg" className={chart.tokens}>
      <div>
        <Group gap="sm" align="baseline" wrap="wrap">
          <Text fz={34} fw={650} lh={1.1} style={{ fontVariantNumeric: 'tabular-nums' }}>
            {numeric ? formatValue(now.value, sensor.unit, locale) : (now.text ?? '–')}
          </Text>
          <Badge variant="light" color={now.stale ? 'yellow' : 'teal'}>
            {now.stale ? t('state.stale') : t('state.live')}
          </Badge>
        </Group>
        {now.stale && (
          <Text size="sm" c="dimmed" mt={4}>
            {t('drawer.staleHint')}
          </Text>
        )}
      </div>

      <section aria-labelledby="sensor-chart">
        <Group justify="space-between" mb={4}>
          <Text id="sensor-chart" fw={600} size="sm">
            {t('drawer.chartTitle')}
          </Text>
          {recent.isFetching && <Loader size={12} color="gray" />}
        </Group>
        {numeric ? (
          <TimeSeriesChart
            series={series}
            from={to - WINDOW_SECONDS * 1000}
            to={to}
            unit={sensor.unit}
            stepMs={1000}
            height={170}
            compact
            locale={locale}
            ariaLabel={t('drawer.chartTitle')}
            formatValue={format}
            empty={
              <Text size="sm" c="dimmed">
                {recent.isPending ? t('drawer.loading') : t('drawer.noHistory')}
              </Text>
            }
          />
        ) : (
          <Text size="sm" c="dimmed">
            {t('drawer.noChart')}
          </Text>
        )}
        {numeric && (
          <Text size="xs" c="dimmed" mt={4}>
            {t('drawer.recordingNote')}
          </Text>
        )}
      </section>

      <section aria-labelledby="sensor-id">
        <Text id="sensor-id" fw={600} size="sm" mb={4}>
          {t('facts.id')}
        </Text>
        <Group gap="xs" wrap="nowrap">
          <Code block style={{ flex: 1, minWidth: 0, overflowWrap: 'anywhere', whiteSpace: 'pre-wrap' }}>
            {sensor.id}
          </Code>
          <CopyButton value={sensor.id} timeout={1600}>
            {({ copied, copy }) => (
              <Tooltip label={copied ? t('drawer.copied') : t('drawer.copyId')}>
                <ActionIcon variant="default" size="lg" onClick={copy} aria-label={t('drawer.copyId')}>
                  {copied ? <IconCheck size={18} /> : <IconCopy size={18} />}
                </ActionIcon>
              </Tooltip>
            )}
          </CopyButton>
        </Group>
      </section>

      <dl className={classes.facts}>
        {facts.map(([label, value]) => (
          <div key={label} style={{ display: 'contents' }}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>

      <Divider />

      <section aria-labelledby="sensor-use">
        <Text id="sensor-use" fw={600} size="sm" mb={4}>
          {t('use.title')}
        </Text>
        <Stack gap={6}>
          <Text size="sm">{t('use.dial')}</Text>
          <Text size="sm">{t('use.text')}</Text>
          <Group gap="xs" wrap="nowrap">
            <Code style={{ overflowWrap: 'anywhere' }}>{placeholder}</Code>
            <CopyButton value={placeholder} timeout={1600}>
              {({ copied, copy }) => (
                <Tooltip label={copied ? t('drawer.copied') : t('use.copyPlaceholder')}>
                  <ActionIcon variant="subtle" color="gray" onClick={copy} aria-label={t('use.copyPlaceholder')}>
                    {copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
                  </ActionIcon>
                </Tooltip>
              )}
            </CopyButton>
          </Group>
        </Stack>
        {numeric && (
          <Button mt="md" variant="light" leftSection={<IconChartLine size={16} />} onClick={() => navigate(`/history?s=${encodeURIComponent(sensor.id)}&r=1h`)}>
            {t('use.openHistory')}
          </Button>
        )}
      </section>
    </Stack>
  );
}
