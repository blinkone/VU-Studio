import { useEffect, useState } from 'react';
import { ActionIcon, Badge, Button, Grid, Group, NumberInput, Paper, Select, SimpleGrid, Slider, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import {
  IconActivity,
  IconAdjustmentsHorizontal,
  IconAntennaBarsOff,
  IconBolt,
  IconChartLine,
  IconDatabase,
  IconFeather,
  IconPlus,
  IconRuler2,
  IconTrash,
  IconWaveSine,
} from '@tabler/icons-react';
import { useSensors, useUnits } from '@/api/queries';
import type { OfflineBehavior, ScalePoint, ScaleType, SensorDto, ValueBinding } from '@/api/types';
import { SensorPicker } from '@/components/SensorPicker';
import { readingParts } from '@/features/dial/format';
import { formatNumber, formatValue } from '@/lib/format';
import { NEEDLE_SPEEDS, SMOOTHING_PRESETS, needleSpeedOf, smoothingPresetOf, type NeedleSpeed, type SmoothingPreset } from './defaults';
import { OptionCards } from './OptionCards';
import { Section } from './Section';
import { AUTO_UNIT, areCompatible, tidy } from './units';
import type { DialEditorModel } from './useDialEditor';
import type { PageReading } from './usePagePreview';
import classes from './editor.module.css';

const SCALES: ScaleType[] = ['linear', 'logarithmic', 'squareRoot', 'custom'];
const OFFLINE: OfflineBehavior[] = ['zero', 'hold', 'dim'];
const notColor = (s: SensorDto) => s.valueKind !== 'color';

const toNumber = (v: string | number): number | undefined => (v === '' || v === null ? undefined : typeof v === 'number' ? v : Number.isFinite(Number(v)) ? Number(v) : undefined);

/** What the needle shows and how it moves. */
export function DataTab({ model, reading }: { model: DialEditorModel; reading: PageReading }) {
  const { t, i18n } = useTranslation('editor');
  const page = model.page!;
  const value = page.value;
  const { sensor, range } = reading;
  const set = (patch: Partial<ValueBinding>) =>
    model.updatePage((p) => {
      Object.assign(p.value, patch);
    });

  const { data: sensors } = useSensors();
  const { data: units } = useUnits(sensor?.unit ? sensor.unit : undefined);
  const suffix = (u: string) => (u ? (u === '%' || u.startsWith('°') ? u : ` ${u}`) : undefined);

  // "Custom" stays selected after the user picks it, even if the times happen to match a preset.
  const [customSmoothing, setCustomSmoothing] = useState(false);
  useEffect(() => setCustomSmoothing(false), [page.id]);
  const smoothing: SmoothingPreset = customSmoothing ? 'custom' : smoothingPresetOf(value);
  const speed = needleSpeedOf(value.needleEasing);

  const onSensorChange = (sensorId: string | undefined, nextUnit: string | undefined) => {
    model.updatePage((p) => {
      const previousUnit = sensor?.unit;
      p.value.sensorId = sensorId;
      // A range or unit chosen for another kind of sensor would be meaningless: start over.
      if (previousUnit !== nextUnit) {
        p.value.min = undefined;
        p.value.max = undefined;
        p.value.points = undefined;
        if (p.value.scale === 'custom') p.value.scale = 'linear';
        if (!nextUnit || !areCompatible(nextUnit, p.value.displayUnit)) p.value.displayUnit = AUTO_UNIT;
      }
    });
  };

  const unitOptions = [
    { value: AUTO_UNIT, label: t('data.unitAuto', { unit: range.unit || '–' }) },
    ...(units ?? []).map((u) => ({ value: u, label: u })),
  ];

  return (
    <Stack gap="lg">
      <Grid gutter="lg">
        <Grid.Col span={{ base: 12, md: 7 }}>
          <Section icon={<IconDatabase size={18} />} title={t('data.source')} description={t('data.sourceHint')}>
            <Stack gap="md">
              <SensorPicker
                label={t('data.sensor')}
                placeholder={t('data.sensorPlaceholder')}
                value={value.sensorId}
                sensorFilter={notColor}
                onChange={(id) => onSensorChange(id, sensors?.find((s) => s.id === id)?.unit)}
              />
              <TextInput
                label={t('data.label')}
                description={t('data.labelHint')}
                placeholder={sensor?.name ?? t('data.labelPlaceholder')}
                value={value.label ?? ''}
                onChange={(e) => set({ label: e.currentTarget.value || undefined })}
                maxLength={60}
              />
            </Stack>
          </Section>
        </Grid.Col>
        <Grid.Col span={{ base: 12, md: 5 }}>
          <LiveReadout reading={reading} decimals={value.decimals} locale={i18n.language} />
        </Grid.Col>
      </Grid>

      <Section icon={<IconRuler2 size={18} />} title={t('data.range')} description={t('data.rangeHint')}>
        <Stack gap="lg">
          <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="md">
            <Select
              label={t('data.unit')}
              data={unitOptions}
              value={value.displayUnit && unitOptions.some((o) => o.value === value.displayUnit) ? value.displayUnit : AUTO_UNIT}
              onChange={(v) => set({ displayUnit: v ?? AUTO_UNIT, min: undefined, max: undefined, points: undefined })}
              allowDeselect={false}
              disabled={!sensor || (units?.length ?? 0) <= 1}
            />
            <NumberInput
              label={t('data.decimals')}
              placeholder={t('data.decimalsAuto')}
              min={0}
              max={6}
              allowDecimal={false}
              value={value.decimals ?? ''}
              onChange={(v) => set({ decimals: toNumber(v) })}
            />
            <NumberInput
              label={t('data.min')}
              placeholder={String(tidy(range.suggestedMin))}
              value={value.min ?? ''}
              suffix={suffix(range.unit)}
              decimalScale={4}
              disabled={value.scale === 'custom'}
              onChange={(v) => set({ min: toNumber(v) })}
            />
            <NumberInput
              label={t('data.max')}
              placeholder={String(tidy(range.suggestedMax))}
              value={value.max ?? ''}
              suffix={suffix(range.unit)}
              decimalScale={4}
              disabled={value.scale === 'custom'}
              onChange={(v) => set({ max: toNumber(v) })}
            />
          </SimpleGrid>

          <OptionCards
            label={t('data.scale')}
            value={value.scale}
            onChange={(scale) =>
              set(
                scale === 'custom' && (value.points?.length ?? 0) < 2
                  ? {
                      scale,
                      points: [
                        { value: tidy(range.min), position: 0 },
                        { value: tidy(range.min + (range.max - range.min) * 0.25), position: 0.5 },
                        { value: tidy(range.max), position: 1 },
                      ],
                    }
                  : { scale },
              )
            }
            options={SCALES.map((s) => ({ value: s, label: t(`data.scales.${s}.label`), description: t(`data.scales.${s}.description`) }))}
            cols={{ base: 2, md: 4 }}
          />

          {value.scale === 'custom' && <ScalePointsEditor points={value.points ?? []} unit={range.unit} onChange={(points) => set({ points })} />}

          <Switch
            label={t('data.invert')}
            description={t('data.invertHint')}
            checked={value.invert}
            onChange={(e) => set({ invert: e.currentTarget.checked })}
          />
        </Stack>
      </Section>

      <Section icon={<IconWaveSine size={18} />} title={t('data.movement')} description={t('data.movementHint')}>
        <Stack gap="lg">
          <OptionCards<SmoothingPreset>
            label={t('data.smoothing')}
            value={smoothing}
            onChange={(preset) => {
              if (preset === 'custom') {
                setCustomSmoothing(true);
              } else {
                setCustomSmoothing(false);
                set(SMOOTHING_PRESETS[preset]);
              }
            }}
            options={(['snappy', 'smooth', 'vu', 'ppm', 'custom'] as const).map((p) => ({
              value: p,
              label: t(`data.smoothings.${p}.label`),
              description: t(`data.smoothings.${p}.description`),
              icon: { snappy: <IconBolt size={16} />, smooth: <IconFeather size={16} />, vu: <IconActivity size={16} />, ppm: <IconChartLine size={16} />, custom: <IconAdjustmentsHorizontal size={16} /> }[p],
            }))}
            cols={{ base: 2, sm: 3, lg: 5 }}
          />
          {smoothing === 'custom' && (
            <SimpleGrid cols={{ base: 1, xs: 3 }} spacing="md">
              <NumberInput
                label={t('data.attack')}
                description={t('data.attackHint')}
                inputWrapperOrder={['label', 'input', 'description']}
                min={0}
                max={10000}
                suffix=" ms"
                value={value.attackMs}
                onChange={(v) => set({ attackMs: toNumber(v) ?? 0 })}
              />
              <NumberInput
                label={t('data.release')}
                description={t('data.releaseHint')}
                inputWrapperOrder={['label', 'input', 'description']}
                min={0}
                max={10000}
                suffix=" ms"
                value={value.releaseMs}
                onChange={(v) => set({ releaseMs: toNumber(v) ?? 0 })}
              />
              <NumberInput
                label={t('data.hold')}
                description={t('data.holdHint')}
                inputWrapperOrder={['label', 'input', 'description']}
                min={0}
                max={10000}
                suffix=" ms"
                value={value.holdMs}
                onChange={(v) => set({ holdMs: toNumber(v) ?? 0 })}
              />
            </SimpleGrid>
          )}

          <OptionCards<NeedleSpeed>
            label={t('data.speed')}
            description={t('data.speedHint')}
            value={speed}
            onChange={(s) => {
              if (s === 'custom') return;
              set({ needleEasing: s === 'device' ? undefined : { ...NEEDLE_SPEEDS[s] } });
            }}
            options={[
              ...(['fast', 'normal', 'lazy', 'device'] as const).map((s) => ({
                value: s as NeedleSpeed,
                label: t(`data.speeds.${s}.label`),
                description: t(`data.speeds.${s}.description`),
              })),
              ...(speed === 'custom'
                ? [
                    {
                      value: 'custom' as NeedleSpeed,
                      label: t('data.speeds.custom.label'),
                      description: t('data.speeds.custom.description', { step: value.needleEasing?.step, period: value.needleEasing?.periodMs }),
                    },
                  ]
                : []),
            ]}
            cols={{ base: 2, md: speed === 'custom' ? 5 : 4 }}
          />
        </Stack>
      </Section>

      <Section icon={<IconAntennaBarsOff size={18} />} title={t('data.offline')} description={t('data.offlineHint')}>
        <OptionCards
          value={value.offline}
          onChange={(offline) => set({ offline })}
          options={OFFLINE.map((o) => ({ value: o, label: t(`data.offlines.${o}.label`), description: t(`data.offlines.${o}.description`) }))}
          cols={{ base: 1, xs: 3 }}
        />
      </Section>
    </Stack>
  );
}

function LiveReadout({ reading, decimals, locale }: { reading: PageReading; decimals: number | undefined; locale: string }) {
  const { t } = useTranslation('editor');
  const parts = readingParts(reading.display, reading.range.unit, reading.text, locale, decimals);
  const raw = reading.live?.v ?? reading.sensor?.value;

  return (
    <Paper withBorder p={{ base: 'md', sm: 'lg' }} h="100%" className={classes.section}>
      <Stack gap="sm" h="100%">
        <Group justify="space-between" gap="xs">
          <Text size="sm" fw={600}>
            {t('data.liveValue')}
          </Text>
          {reading.sensor &&
            (reading.stale ? (
              <Badge color="yellow" variant="light" size="sm">
                {t('data.stale')}
              </Badge>
            ) : (
              <Badge color="teal" variant="dot" size="sm">
                {t('data.live')}
              </Badge>
            ))}
        </Group>
        {!reading.sensor ? (
          <Text size="sm" c="dimmed">
            {t('data.noSensor')}
          </Text>
        ) : parts.empty ? (
          <Text size="sm" c="dimmed">
            {t('data.noValue')}
          </Text>
        ) : (
          <div className={classes.readout} aria-live="off">
            {parts.value}
            {parts.unit && <span className={classes.readoutUnit}>{parts.unit}</span>}
          </div>
        )}
        {reading.sensor && raw !== undefined && raw !== null && reading.range.unit !== reading.range.canonical && (
          <Text size="xs" c="dimmed">
            {t('data.raw', { value: formatValue(raw, reading.range.canonical, locale) })}
          </Text>
        )}
        <div style={{ marginTop: 'auto' }}>
          <Group justify="space-between" mb={6}>
            <Text size="xs" c="dimmed">
              {t('data.needleAt')}
            </Text>
            <Text size="xs" fw={600} ff="monospace">
              {Math.round(reading.fraction * 100)}%
            </Text>
          </Group>
          <div className={classes.gauge}>
            <div className={classes.gaugeFill} style={{ width: `${reading.fraction * 100}%` }} />
          </div>
          <Group justify="space-between" mt={6}>
            <Text size="xs" c="dimmed" ff="monospace">
              {formatNumber(reading.range.min, undefined, locale)}
            </Text>
            <Text size="xs" c="dimmed" ff="monospace">
              {formatNumber(reading.range.max, undefined, locale)} {reading.range.unit}
            </Text>
          </Group>
        </div>
      </Stack>
    </Paper>
  );
}

function ScalePointsEditor({ points, unit, onChange }: { points: ScalePoint[]; unit: string; onChange: (points: ScalePoint[]) => void }) {
  const { t } = useTranslation('editor');
  const set = (index: number, patch: Partial<ScalePoint>) => onChange(points.map((p, i) => (i === index ? { ...p, ...patch } : p)));
  const add = () => {
    const sorted = [...points].sort((a, b) => a.value - b.value);
    const last = sorted[sorted.length - 1];
    const prev = sorted[sorted.length - 2];
    const value = last ? tidy(last.value + (prev ? last.value - prev.value : 10)) : 0;
    onChange([...points, { value, position: 1 }]);
  };

  return (
    <Stack gap="xs">
      <div>
        <Text size="sm" fw={500}>
          {t('data.points')}
        </Text>
        <Text size="xs" c="dimmed">
          {t('data.pointsHint')}
        </Text>
      </div>
      {points.map((point, i) => (
        <Group key={i} className={classes.row} gap="sm" wrap="nowrap">
          <NumberInput
            w={130}
            size="sm"
            value={point.value}
            decimalScale={4}
            suffix={unit ? ` ${unit}` : undefined}
            onChange={(v) => set(i, { value: toNumber(v) ?? 0 })}
            aria-label={t('data.pointValue', { index: i + 1 })}
          />
          <Slider
            style={{ flex: 1 }}
            min={0}
            max={100}
            value={Math.round(point.position * 100)}
            onChange={(v) => set(i, { position: v / 100 })}
            label={(v) => `${v}%`}
            aria-label={t('data.pointPosition', { index: i + 1 })}
          />
          <Text size="xs" ff="monospace" c="dimmed" w={36} ta="right">
            {Math.round(point.position * 100)}%
          </Text>
          <Tooltip label={t('data.removePoint')}>
            <ActionIcon variant="subtle" color="red" onClick={() => onChange(points.filter((_, j) => j !== i))} aria-label={t('data.removePoint')}>
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
        </Group>
      ))}
      {points.length < 2 && (
        <Text size="xs" c="yellow">
          {t('data.pointsTooFew')}
        </Text>
      )}
      <Group>
        <Button variant="light" size="xs" leftSection={<IconPlus size={14} />} onClick={add}>
          {t('data.addPoint')}
        </Button>
      </Group>
    </Stack>
  );
}
