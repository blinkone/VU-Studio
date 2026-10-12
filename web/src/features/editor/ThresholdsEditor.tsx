import { ActionIcon, Button, Group, NumberInput, Stack, Text, Tooltip } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import type { RgbwHex, ThresholdStep } from '@/api/types';
import { tidy } from './units';
import { RgbwColorInput } from './RgbwColorInput';
import classes from './editor.module.css';

interface ThresholdsEditorProps {
  thresholds: ThresholdStep[];
  onChange: (thresholds: ThresholdStep[]) => void;
  /** Color below the first threshold (BacklightConfig.color). */
  baseColor: RgbwHex;
  onBaseColorChange: (color: RgbwHex) => void;
  unit: string;
  min: number;
  max: number;
}

/** Color steps by value: the highest threshold the value reaches wins. */
export function ThresholdsEditor({ thresholds, onChange, baseColor, onBaseColorChange, unit, min, max }: ThresholdsEditorProps) {
  const { t } = useTranslation('editor');
  const suffix = unit ? (unit === '%' || unit.startsWith('°') ? unit : ` ${unit}`) : undefined;

  const add = () => {
    const last = thresholds.length > 0 ? Math.max(...thresholds.map((s) => s.value)) : undefined;
    const step = (max - min) / 5;
    const value = tidy(last === undefined ? min + (max - min) * 0.6 : Math.min(max, last + step));
    onChange([...thresholds, { value, color: thresholds.length === 0 ? '#FFD200' : '#FF0000' }]);
  };

  return (
    <Stack gap="sm">
      <Group className={classes.row} justify="space-between" wrap="wrap" gap="sm">
        <Text size="sm">{t('light.below')}</Text>
        <RgbwColorInput compact value={baseColor} onChange={onBaseColorChange} ariaLabel={t('light.below')} />
      </Group>
      {thresholds.map((step, i) => (
        <Group key={i} className={classes.row} gap="sm" wrap="nowrap">
          <NumberInput
            style={{ flex: 1, minWidth: 0 }}
            size="sm"
            leftSection={
              <Text size="xs" c="dimmed" pl={6}>
                {t('light.from')}
              </Text>
            }
            leftSectionWidth={52}
            value={step.value}
            suffix={suffix}
            decimalScale={3}
            onChange={(v) => onChange(thresholds.map((s, j) => (j === i ? { ...s, value: typeof v === 'number' ? v : Number(v) || 0 } : s)))}
            aria-label={t('light.thresholdValue', { index: i + 1 })}
          />
          <RgbwColorInput
            compact
            value={step.color}
            onChange={(color) => onChange(thresholds.map((s, j) => (j === i ? { ...s, color } : s)))}
            ariaLabel={t('light.thresholdColor', { index: i + 1 })}
          />
          <Tooltip label={t('light.removeThreshold')}>
            <ActionIcon variant="subtle" color="red" onClick={() => onChange(thresholds.filter((_, j) => j !== i))} aria-label={t('light.removeThreshold')}>
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
        </Group>
      ))}
      {thresholds.length === 0 && (
        <Text size="sm" c="dimmed">
          {t('light.noThresholds')}
        </Text>
      )}
      <Group>
        <Button variant="light" size="xs" leftSection={<IconPlus size={14} />} onClick={add}>
          {t('light.addThreshold')}
        </Button>
      </Group>
    </Stack>
  );
}
