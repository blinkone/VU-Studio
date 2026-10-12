import { useEffect, useState, type CSSProperties } from 'react';
import { Badge, ColorPicker, Group, Input, Popover, Slider, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useId } from '@mantine/hooks';
import { useTranslation } from 'react-i18next';
import type { RgbwHex } from '@/api/types';
import { formatRgbw, parseRgbw, rgbHex, rgbwPreview, whitePercent, withWhitePercent, type Rgbw } from '@/features/dial/color';
import { COLOR_SWATCHES } from './defaults';
import classes from './editor.module.css';

interface RgbwColorInputProps {
  value: RgbwHex;
  onChange: (value: RgbwHex) => void;
  label?: string;
  description?: string;
  /** Accessible label when there is no visible label. */
  ariaLabel?: string;
  /** Swatch and hex only, without the full-width field. */
  compact?: boolean;
  disabled?: boolean;
}

/**
 * Picks a backlight color: RGB with a color picker, plus the separate white LED (0–100 %, stored as
 * the "+WW" suffix of "#RRGGBB+WW"). The swatch shows how the light actually looks.
 */
export function RgbwColorInput({ value, onChange, label, description, ariaLabel, compact, disabled }: RgbwColorInputProps) {
  const { t } = useTranslation('editor');
  const id = useId();
  const color: Rgbw = parseRgbw(value) ?? { r: 0, g: 0, b: 0, w: 0 };
  const preview = rgbwPreview(color);
  const white = whitePercent(color);
  const [hexText, setHexText] = useState(rgbHex(color));
  const [opened, setOpened] = useState(false);

  useEffect(() => setHexText(rgbHex(color)), [color.r, color.g, color.b]);

  const setRgb = (hex: string) => {
    const rgb = parseRgbw(hex);
    if (rgb) onChange(formatRgbw({ ...rgb, w: color.w }));
  };

  const button = (
    <UnstyledButton
      id={id}
      className={classes.colorButton}
      data-compact={compact || undefined}
      disabled={disabled}
      onClick={() => setOpened((o) => !o)}
      aria-label={ariaLabel ?? label ?? t('color.pick')}
      aria-haspopup="dialog"
      aria-expanded={opened}
      style={{ '--swatch-glow': preview } as CSSProperties}
    >
      <span className={classes.swatch} style={{ background: preview }} />
      <span className={classes.hex}>{rgbHex(color)}</span>
      {white > 0 && (
        <Badge size="xs" variant="default" radius="sm" style={{ flex: 'none' }}>
          W {white}%
        </Badge>
      )}
    </UnstyledButton>
  );

  return (
    <Input.Wrapper label={label} description={description} labelProps={{ htmlFor: id }}>
      <Popover opened={opened} onChange={setOpened} position="bottom-start" shadow="lg" withinPortal trapFocus returnFocus>
        <Popover.Target>{button}</Popover.Target>
        <Popover.Dropdown p="sm" aria-label={label ?? ariaLabel ?? t('color.pick')}>
          <Stack gap="sm" w={232}>
            <ColorPicker
              format="hex"
              value={rgbHex(color)}
              onChange={setRgb}
              fullWidth
              saturationLabel={t('color.saturation')}
              hueLabel={t('color.hue')}
            />
            <TextInput
              size="xs"
              label={t('color.hex')}
              value={hexText}
              onChange={(e) => {
                setHexText(e.currentTarget.value);
                if (/^#?[0-9a-f]{6}$/i.test(e.currentTarget.value.trim())) setRgb(e.currentTarget.value.trim());
              }}
              onBlur={() => setHexText(rgbHex(color))}
              styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
            />
            <div>
              <Group justify="space-between" mb={4}>
                <Text size="xs" fw={500}>
                  {t('color.white')}
                </Text>
                <Text size="xs" c="dimmed" ff="monospace">
                  {white}%
                </Text>
              </Group>
              <Slider
                size="sm"
                min={0}
                max={100}
                value={white}
                onChange={(v) => onChange(formatRgbw(withWhitePercent(color, v)))}
                label={(v) => `${v}%`}
                color="gray"
                aria-label={t('color.white')}
              />
              <Text size="xs" c="dimmed" mt={4}>
                {t('color.whiteHint')}
              </Text>
            </div>
            <Group gap={6} justify="space-between">
              {COLOR_SWATCHES.map((swatch) => (
                <button
                  key={swatch}
                  type="button"
                  className={classes.presetSwatch}
                  style={{ background: rgbwPreview(swatch) }}
                  onClick={() => onChange(swatch)}
                  aria-label={swatch}
                  title={swatch}
                />
              ))}
            </Group>
          </Stack>
        </Popover.Dropdown>
      </Popover>
    </Input.Wrapper>
  );
}
