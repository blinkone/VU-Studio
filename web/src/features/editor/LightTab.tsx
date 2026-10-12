import type { CSSProperties, ReactNode } from 'react';
import { Grid, Group, NumberInput, Paper, SimpleGrid, Slider, Stack, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import {
  IconAlarm,
  IconBrightnessHalf,
  IconBulb,
  IconBulbOff,
  IconColorSwatch,
  IconPalette,
  IconRefresh,
  IconStairs,
  IconSun,
} from '@tabler/icons-react';
import { useDialState } from '@/api/hooks';
import type { BacklightConfig, BacklightMode, LightEffect, SyncSource } from '@/api/types';
import { SensorPicker } from '@/components/SensorPicker';
import { isDark } from '@/features/dial/color';
import { AlarmsEditor } from './AlarmsEditor';
import { GradientEditor } from './GradientEditor';
import { OptionCards } from './OptionCards';
import { RgbwColorInput } from './RgbwColorInput';
import { Section } from './Section';
import { ThresholdsEditor } from './ThresholdsEditor';
import type { DialEditorModel } from './useDialEditor';
import type { PageReading } from './usePagePreview';
import classes from './editor.module.css';

const MODES: BacklightMode[] = ['off', 'fixed', 'gradient', 'thresholds', 'sync'];
const EFFECTS: LightEffect[] = ['none', 'breathe', 'pulse', 'blink', 'rainbow'];
const SYNCS: SyncSource[] = ['accentColor', 'albumArt', 'sensor'];

const MODE_ICONS: Record<BacklightMode, ReactNode> = {
  off: <IconBulbOff size={16} />,
  fixed: <IconBulb size={16} />,
  gradient: <IconPalette size={16} />,
  thresholds: <IconStairs size={16} />,
  sync: <IconRefresh size={16} />,
};

/** Backlight mode, colors, brightness, effect and alarms of the selected page. */
export function LightTab({ model, reading }: { model: DialEditorModel; reading: PageReading }) {
  const { t } = useTranslation('editor');
  const page = model.page!;
  const light = page.light;
  const set = (patch: Partial<BacklightConfig>) =>
    model.updatePage((p) => {
      Object.assign(p.light, patch);
    });
  const num = (v: string | number, fallback: number) => (typeof v === 'number' ? v : v === '' ? fallback : Number(v) || fallback);

  return (
    <Stack gap="lg">
      <Grid gutter="lg">
        <Grid.Col span={{ base: 12, lg: 8 }}>
          <Section icon={<IconColorSwatch size={18} />} title={t('light.mode')} description={t('light.modeHint')}>
            <Stack gap="lg">
              <OptionCards
                value={light.mode}
                onChange={(mode) => set({ mode })}
                options={MODES.map((m) => ({ value: m, label: t(`light.modes.${m}.label`), description: t(`light.modes.${m}.description`), icon: MODE_ICONS[m] }))}
                cols={{ base: 2, sm: 3, md: 5 }}
              />

              {light.mode === 'fixed' && (
                <RgbwColorInput label={t('light.color')} value={light.color} onChange={(color) => set({ color })} />
              )}

              {light.mode === 'gradient' && (
                <GradientEditor stops={light.gradient} onChange={(gradient) => set({ gradient })} needle={reading.display !== undefined ? reading.fraction : undefined} />
              )}

              {light.mode === 'thresholds' && (
                <Stack gap="xs">
                  <div>
                    <Text size="sm" fw={500}>
                      {t('light.thresholds')}
                    </Text>
                    <Text size="xs" c="dimmed">
                      {t('light.thresholdsHint')}
                    </Text>
                  </div>
                  <ThresholdsEditor
                    thresholds={light.thresholds}
                    onChange={(thresholds) => set({ thresholds })}
                    baseColor={light.color}
                    onBaseColorChange={(color) => set({ color })}
                    unit={reading.range.unit}
                    min={reading.range.min}
                    max={reading.range.max}
                  />
                </Stack>
              )}

              {light.mode === 'sync' && (
                <Stack gap="md">
                  <OptionCards
                    label={t('light.sync')}
                    value={light.sync}
                    onChange={(sync) => set({ sync })}
                    options={SYNCS.map((s) => ({ value: s, label: t(`light.syncs.${s}.label`), description: t(`light.syncs.${s}.description`) }))}
                    cols={{ base: 1, xs: 3 }}
                  />
                  {light.sync === 'sensor' && (
                    <SensorPicker
                      label={t('light.syncSensor')}
                      placeholder={t('light.syncSensorPlaceholder')}
                      value={light.syncSensorId}
                      onChange={(syncSensorId) => set({ syncSensorId })}
                      sensorFilter={(s) => s.valueKind === 'color'}
                    />
                  )}
                  <RgbwColorInput label={t('light.fallback')} description={t('light.fallbackHint')} value={light.color} onChange={(color) => set({ color })} />
                </Stack>
              )}

              {light.mode === 'off' && (
                <Text size="sm" c="dimmed">
                  {t('light.offHint')}
                </Text>
              )}
            </Stack>
          </Section>
        </Grid.Col>
        <Grid.Col span={{ base: 12, lg: 4 }}>
          <LiveColor model={model} />
        </Grid.Col>
      </Grid>

      <Section icon={<IconSun size={18} />} title={t('light.output')} description={t('light.outputHint')}>
        <Stack gap="lg">
          <div>
            <Group justify="space-between" mb={6}>
              <Text size="sm" fw={500} id="brightness-label">
                {t('light.brightness')}
              </Text>
              <Text size="sm" ff="monospace" c="dimmed">
                {light.brightness}%
              </Text>
            </Group>
            <Slider
              min={0}
              max={100}
              value={light.brightness}
              onChange={(brightness) => set({ brightness })}
              label={(v) => `${v}%`}
              marks={[{ value: 0 }, { value: 25 }, { value: 50 }, { value: 75 }, { value: 100 }]}
              thumbChildren={<IconBrightnessHalf size={12} />}
              thumbSize={22}
              aria-labelledby="brightness-label"
            />
          </div>

          <OptionCards
            label={t('light.effect')}
            value={light.effect}
            onChange={(effect) => set({ effect })}
            options={EFFECTS.map((e) => ({ value: e, label: t(`light.effects.${e}`), description: t(`light.effectHints.${e}`) }))}
            cols={{ base: 2, sm: 3, md: 5 }}
          />

          {light.effect !== 'none' && (
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg">
              <NumberInput
                label={t('light.period')}
                description={t('light.periodHint')}
                inputWrapperOrder={['label', 'input', 'description']}
                min={0.1}
                max={120}
                step={0.5}
                decimalScale={1}
                suffix=" s"
                value={light.effectPeriodMs / 1000}
                onChange={(v) => set({ effectPeriodMs: Math.round(Math.min(120, Math.max(0.1, num(v, 4))) * 1000) })}
              />
              {light.effect !== 'rainbow' && (
                <div>
                  <Group justify="space-between" mb={4}>
                    <Text size="sm" fw={500} id="depth-label">
                      {t('light.depth')}
                    </Text>
                    <Text size="sm" ff="monospace" c="dimmed">
                      {light.effectDepth}%
                    </Text>
                  </Group>
                  <Slider min={0} max={100} value={light.effectDepth} onChange={(effectDepth) => set({ effectDepth })} label={(v) => `${v}%`} aria-labelledby="depth-label" />
                  <Text size="xs" c="dimmed" mt={6}>
                    {t('light.depthHint')}
                  </Text>
                </div>
              )}
            </SimpleGrid>
          )}
        </Stack>
      </Section>

      <Section icon={<IconAlarm size={18} />} title={t('light.alarms')} description={t('light.alarmsHint')}>
        <AlarmsEditor alarms={light.alarms} onChange={(alarms) => set({ alarms })} unit={reading.range.unit} min={reading.range.min} max={reading.range.max} />
      </Section>
    </Stack>
  );
}

/** The color the dial is actually showing (from the engine), when it is showing this page. */
function LiveColor({ model }: { model: DialEditorModel }) {
  const { t } = useTranslation('editor');
  const dial = useDialState(model.slot);
  const showing = !!dial && dial.profileId === model.profile.id && dial.pageId === model.page?.id;
  const off = !dial || isDark(dial.light);

  return (
    <Paper withBorder p={{ base: 'md', sm: 'lg' }} h="100%" className={classes.section}>
      <Stack gap="md">
        <div>
          <Text size="sm" fw={600}>
            {t('light.now')}
          </Text>
          <Text size="xs" c="dimmed">
            {showing ? t('light.nowHint') : t('light.nowOther')}
          </Text>
        </div>
        <Group gap="md" wrap="nowrap">
          <div
            className={classes.nowSwatch}
            style={{ background: off ? 'var(--mantine-color-dark-6)' : dial!.previewColor, '--swatch-glow': off ? 'transparent' : dial!.previewColor } as CSSProperties}
            role="img"
            aria-label={off ? t('light.nowOff') : dial!.previewColor}
          />
          <div>
            <Text size="lg" fw={700} ff="monospace">
              {off ? t('light.nowOff') : dial!.previewColor}
            </Text>
            {dial?.alarmActive && (
              <Text size="xs" c="red" fw={600}>
                {t('light.alarmActive')}
              </Text>
            )}
          </div>
        </Group>
        <Stack gap={6}>
          {(['r', 'g', 'b', 'w'] as const).map((ch) => (
            <div key={ch} className={classes.level}>
              <span>{ch.toUpperCase()}</span>
              <div className={classes.gauge} style={{ height: 6 }}>
                <div
                  className={classes.gaugeFill}
                  style={{
                    width: `${dial?.light[ch] ?? 0}%`,
                    background: { r: '#ff4d4d', g: '#3ddc84', b: '#4d8dff', w: 'var(--mantine-color-gray-4)' }[ch],
                  }}
                />
              </div>
              <span style={{ textAlign: 'right' }}>{dial?.light[ch] ?? 0}</span>
            </div>
          ))}
        </Stack>
      </Stack>
    </Paper>
  );
}
