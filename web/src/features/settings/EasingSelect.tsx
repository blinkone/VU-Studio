import { useEffect, useState } from 'react';
import { Input, SegmentedControl, SimpleGrid, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import type { Easing } from '@/api/types';
import { NumField } from './fields';
import { EASING_PRESETS, PRESET_ORDER, easingSpeed, matchPreset, type EasingKind, type EasingPreset } from './dials';

const isPreset = (value: string): value is EasingPreset => (PRESET_ORDER as readonly string[]).includes(value);

interface EasingSelectProps {
  kind: EasingKind;
  label: string;
  /** `undefined` = the factory default. */
  value: Easing | undefined;
  onChange: (easing: Easing | undefined) => void;
  disabled?: boolean;
}

/**
 * Motion presets for a dial's needle or backlight: Fast, Normal (factory default), Lazy, or custom
 * step/period values. The dial moves `step` % every `period` ms.
 */
export function EasingSelect({ kind, label, value, onChange, disabled }: EasingSelectProps) {
  const { t } = useTranslation('settings');
  const matched = matchPreset(kind, value);
  const [custom, setCustom] = useState(matched === 'custom');

  // Follow changes made elsewhere (discarding edits, reloading the configuration).
  useEffect(() => {
    if (matched === 'custom') setCustom(true);
  }, [matched]);

  const mode = custom ? 'custom' : matched;
  const effective = value ?? EASING_PRESETS[kind].normal;

  return (
    <Input.Wrapper label={label}>
      <SegmentedControl
        mt={6}
        size="xs"
        fullWidth
        disabled={disabled}
        value={mode}
        aria-label={label}
        data={[
          ...PRESET_ORDER.map((preset) => ({ value: preset, label: t(`easing.${preset}`) })),
          { value: 'custom', label: t('easing.custom') },
        ]}
        onChange={(next) => {
          if (!isPreset(next)) {
            setCustom(true);
            return;
          }
          setCustom(false);
          // Normal is the factory default: store nothing rather than a copy of it.
          onChange(next === 'normal' ? undefined : { ...EASING_PRESETS[kind][next] });
        }}
      />
      {mode === 'custom' && (
        <SimpleGrid cols={2} spacing="xs" mt="xs">
          <NumField
            size="xs"
            label={t('easing.step')}
            value={effective.step}
            min={1}
            max={100}
            allowDecimal={false}
            suffix=" %"
            disabled={disabled}
            onCommit={(step) => onChange({ ...effective, step })}
          />
          <NumField
            size="xs"
            label={t('easing.period')}
            value={effective.periodMs}
            min={10}
            max={2000}
            allowDecimal={false}
            suffix=" ms"
            disabled={disabled}
            onCommit={(periodMs) => onChange({ ...effective, periodMs })}
          />
        </SimpleGrid>
      )}
      <Text size="xs" c="dimmed" mt={4}>
        {t('easing.speed', { step: effective.step, period: effective.periodMs, speed: easingSpeed(effective) })}
      </Text>
    </Input.Wrapper>
  );
}
