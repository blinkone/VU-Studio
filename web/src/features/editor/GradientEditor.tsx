import { ActionIcon, Button, Group, Menu, Slider, Stack, Text, Tooltip } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconChevronDown, IconPlus, IconTrash } from '@tabler/icons-react';
import type { GradientStop } from '@/api/types';
import { formatRgbw, gradientCss, rgbwPreview, sampleGradient } from '@/features/dial/color';
import { GRADIENT_PRESETS } from './defaults';
import { RgbwColorInput } from './RgbwColorInput';
import classes from './editor.module.css';

interface GradientEditorProps {
  stops: GradientStop[];
  onChange: (stops: GradientStop[]) => void;
  /** Current needle position 0..1, shown as a marker on the bar. */
  needle?: number;
}

/** Edits the colors along the dial scale: a preview bar plus one row per stop. */
export function GradientEditor({ stops, onChange, needle }: GradientEditorProps) {
  const { t } = useTranslation('editor');

  const setStop = (index: number, patch: Partial<GradientStop>) => onChange(stops.map((s, i) => (i === index ? { ...s, ...patch } : s)));

  const addStop = () => {
    // Insert in the middle of the widest gap, with the color the gradient already has there.
    const positions = [0, ...stops.map((s) => s.position).sort((a, b) => a - b), 1];
    let best = 0.5;
    let widest = -1;
    for (let i = 1; i < positions.length; i++) {
      const gap = positions[i] - positions[i - 1];
      if (gap > widest) {
        widest = gap;
        best = (positions[i] + positions[i - 1]) / 2;
      }
    }
    const position = Math.round(best * 100) / 100;
    onChange([...stops, { position, color: formatRgbw(sampleGradient(stops, position)) }].sort((a, b) => a.position - b.position));
  };

  return (
    <Stack gap="md">
      <Group justify="space-between" gap="xs">
        <Text size="sm" fw={500}>
          {t('light.gradient')}
        </Text>
        <Menu position="bottom-end" withinPortal>
          <Menu.Target>
            <Button variant="subtle" size="compact-sm" rightSection={<IconChevronDown size={14} />}>
              {t('light.presets')}
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            {Object.entries(GRADIENT_PRESETS).map(([key, preset]) => (
              <Menu.Item
                key={key}
                onClick={() => onChange(preset.map((s) => ({ ...s })))}
                leftSection={<span style={{ display: 'block', width: 40, height: 12, borderRadius: 6, background: gradientCss(preset) }} />}
              >
                {t(`light.gradientPresets.${key}`)}
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
      </Group>

      <div>
        <div className={classes.gradientBar} style={{ background: gradientCss(stops) }} role="img" aria-label={t('light.gradient')}>
          {stops.map((s, i) => (
            <span key={i} className={classes.gradientStop} style={{ left: `${s.position * 100}%`, background: rgbwPreview(s.color) }} />
          ))}
          {needle !== undefined && (
            <Tooltip label={t('light.needleHere')}>
              <span className={classes.needleMarker} style={{ left: `${needle * 100}%`, pointerEvents: 'auto' }} />
            </Tooltip>
          )}
        </div>
        <Group justify="space-between" mt={6}>
          <Text size="xs" c="dimmed">
            {t('light.scaleStart')}
          </Text>
          <Text size="xs" c="dimmed">
            {t('light.scaleEnd')}
          </Text>
        </Group>
      </div>

      <Stack gap="xs">
        {stops.map((stop, i) => (
          <Group key={i} className={classes.row} gap="sm" wrap="nowrap">
            <RgbwColorInput compact value={stop.color} onChange={(color) => setStop(i, { color })} ariaLabel={t('light.stopColor', { index: i + 1 })} />
            <Slider
              style={{ flex: 1 }}
              min={0}
              max={100}
              value={Math.round(stop.position * 100)}
              onChange={(v) => setStop(i, { position: v / 100 })}
              label={(v) => `${v}%`}
              aria-label={t('light.stopPosition', { index: i + 1 })}
            />
            <Text size="xs" ff="monospace" c="dimmed" w={36} ta="right">
              {Math.round(stop.position * 100)}%
            </Text>
            <Tooltip label={t('light.removeStop')}>
              <ActionIcon
                variant="subtle"
                color="red"
                disabled={stops.length <= 1}
                onClick={() => onChange(stops.filter((_, j) => j !== i))}
                aria-label={t('light.removeStop')}
              >
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </Group>
        ))}
      </Stack>
      <Group>
        <Button variant="light" size="xs" leftSection={<IconPlus size={14} />} onClick={addStop} disabled={stops.length >= 8}>
          {t('light.addStop')}
        </Button>
      </Group>
    </Stack>
  );
}
