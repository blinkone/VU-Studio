import type { CSSProperties } from 'react';
import { ActionIcon, Button, Group, NumberInput, Paper, SegmentedControl, Select, SimpleGrid, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconBellRinging, IconPlus, IconTrash } from '@tabler/icons-react';
import type { AlarmRule, LightEffect } from '@/api/types';
import { SensorPicker } from '@/components/SensorPicker';
import { rgbwPreview } from '@/features/dial/color';
import { formatReading } from '@/features/dial/format';
import { createAlarm } from './defaults';
import { RgbwColorInput } from './RgbwColorInput';
import { tidy } from './units';
import classes from './editor.module.css';

const EFFECTS: LightEffect[] = ['none', 'breathe', 'pulse', 'blink', 'rainbow'];

interface AlarmsEditorProps {
  alarms: AlarmRule[];
  onChange: (alarms: AlarmRule[]) => void;
  /** Unit and range of the page value (alarms without a sensor watch the page value). */
  unit: string;
  min: number;
  max: number;
}

/** Rules that take over the light (and optionally notify) when a value crosses a threshold. */
export function AlarmsEditor({ alarms, onChange, unit, min, max }: AlarmsEditorProps) {
  const { t, i18n } = useTranslation('editor');
  const set = (index: number, patch: Partial<AlarmRule>) => onChange(alarms.map((a, i) => (i === index ? { ...a, ...patch } : a)));
  const num = (v: string | number, fallback = 0) => (typeof v === 'number' ? v : v === '' ? fallback : Number(v) || fallback);

  return (
    <Stack gap="sm">
      {alarms.length === 0 && (
        <Text size="sm" c="dimmed">
          {t('light.noAlarms')}
        </Text>
      )}
      {alarms.map((alarm, i) => {
        const pageValue = !alarm.sensorId;
        const suffix = pageValue && unit ? (unit === '%' || unit.startsWith('°') ? unit : ` ${unit}`) : undefined;
        const summary = t(alarm.comparison === 'above' ? 'alarm.summaryAbove' : 'alarm.summaryBelow', {
          value: pageValue ? formatReading(alarm.threshold, unit, undefined, i18n.language) : alarm.threshold,
        });
        return (
          <Paper
            key={i}
            withBorder
            p="md"
            className={`${classes.row} ${classes.alarm}`}
            data-disabled={!alarm.enabled || undefined}
            style={{ '--alarm-color': rgbwPreview(alarm.color) } as CSSProperties}
          >
            <Group justify="space-between" wrap="nowrap" mb="sm" gap="sm">
              <Group gap="sm" wrap="nowrap" miw={0}>
                <Switch
                  checked={alarm.enabled}
                  onChange={(e) => set(i, { enabled: e.currentTarget.checked })}
                  aria-label={t('alarm.enabled')}
                />
                <div style={{ minWidth: 0 }}>
                  <Text size="sm" fw={600}>
                    {t('alarm.title', { index: i + 1 })}
                  </Text>
                  <Text size="xs" c="dimmed" truncate>
                    {summary}
                  </Text>
                </div>
              </Group>
              <Tooltip label={t('alarm.remove')}>
                <ActionIcon variant="subtle" color="red" onClick={() => onChange(alarms.filter((_, j) => j !== i))} aria-label={t('alarm.remove')}>
                  <IconTrash size={16} />
                </ActionIcon>
              </Tooltip>
            </Group>

            <Stack gap="sm">
              <SensorPicker
                label={t('alarm.watch')}
                placeholder={t('alarm.pageValue')}
                value={alarm.sensorId}
                onChange={(sensorId) => set(i, { sensorId })}
                sensorFilter={(s) => s.valueKind === 'number'}
                description={pageValue ? t('alarm.pageValueHint') : t('alarm.sensorHint')}
              />
              <SimpleGrid cols={{ base: 1, xs: 2, md: 4 }} spacing="sm">
                <Stack gap={4}>
                  <Text size="sm" fw={500}>
                    {t('alarm.comparison')}
                  </Text>
                  <SegmentedControl
                    fullWidth
                    size="sm"
                    value={alarm.comparison}
                    onChange={(v) => set(i, { comparison: v as AlarmRule['comparison'] })}
                    data={[
                      { value: 'above', label: t('alarm.above') },
                      { value: 'below', label: t('alarm.below') },
                    ]}
                    aria-label={t('alarm.comparison')}
                  />
                </Stack>
                <NumberInput label={t('alarm.threshold')} value={alarm.threshold} suffix={suffix} decimalScale={3} onChange={(v) => set(i, { threshold: num(v) })} />
                <NumberInput
                  label={t('alarm.hysteresis')}
                  description={t('alarm.hysteresisHint')}
                  inputWrapperOrder={['label', 'input', 'description']}
                  min={0}
                  value={alarm.hysteresis}
                  suffix={suffix}
                  decimalScale={3}
                  onChange={(v) => set(i, { hysteresis: Math.abs(num(v)) })}
                />
                <NumberInput
                  label={t('alarm.forSeconds')}
                  min={0}
                  value={alarm.forSeconds}
                  suffix=" s"
                  decimalScale={1}
                  onChange={(v) => set(i, { forSeconds: Math.max(0, num(v)) })}
                />
              </SimpleGrid>
              <SimpleGrid cols={{ base: 1, xs: 3 }} spacing="sm">
                <RgbwColorInput label={t('alarm.color')} value={alarm.color} onChange={(color) => set(i, { color })} />
                <Select
                  label={t('alarm.effect')}
                  allowDeselect={false}
                  value={alarm.effect}
                  onChange={(v) => v && set(i, { effect: v as LightEffect })}
                  data={EFFECTS.map((e) => ({ value: e, label: t(`light.effects.${e}`) }))}
                />
                <NumberInput
                  label={t('alarm.period')}
                  min={0.1}
                  max={120}
                  step={0.1}
                  decimalScale={1}
                  suffix=" s"
                  disabled={alarm.effect === 'none'}
                  value={alarm.periodMs / 1000}
                  onChange={(v) => set(i, { periodMs: Math.round(Math.max(0.1, num(v, 0.8)) * 1000) })}
                />
              </SimpleGrid>
              <Switch
                label={t('alarm.notify')}
                checked={alarm.notify}
                onChange={(e) => set(i, { notify: e.currentTarget.checked })}
                thumbIcon={alarm.notify ? <IconBellRinging size={10} /> : undefined}
              />
              {alarm.notify && (
                <TextInput
                  label={t('alarm.message')}
                  description={t('alarm.messageHint')}
                  inputWrapperOrder={['label', 'input', 'description']}
                  placeholder="{title}: {value}{unit}"
                  value={alarm.message ?? ''}
                  onChange={(e) => set(i, { message: e.currentTarget.value || undefined })}
                />
              )}
            </Stack>
          </Paper>
        );
      })}
      <Group>
        <Button variant="light" size="xs" leftSection={<IconPlus size={14} />} onClick={() => onChange([...alarms, createAlarm(tidy(min + (max - min) * 0.9))])}>
          {t('alarm.add')}
        </Button>
      </Group>
    </Stack>
  );
}
