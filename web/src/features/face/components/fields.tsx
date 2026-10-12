import { useEffect, useState, type ComponentType, type ReactNode } from 'react';
import {
  Center,
  Group,
  Input,
  NumberInput,
  SegmentedControl,
  Select,
  Slider,
  Stack,
  Switch,
  Text,
  Tooltip,
  VisuallyHidden,
} from '@mantine/core';
import type { IconProps } from '@tabler/icons-react';
import classes from '../FaceEditor.module.css';

// Compact controls for the property panels.

export function Section({ title, children, action }: { title: ReactNode; children: ReactNode; action?: ReactNode }) {
  return (
    <Stack gap={8}>
      <Group justify="space-between" gap="xs" wrap="nowrap" mih={22}>
        <Text className={classes.sectionTitle}>{title}</Text>
        {action}
      </Group>
      {children}
    </Stack>
  );
}

export function PropGrid({ children }: { children: ReactNode }) {
  return <div className={classes.propGrid}>{children}</div>;
}

interface NumFieldProps {
  label?: ReactNode;
  /** Short axis-style label shown inside the input (X, Y, W…), instead of a label above. */
  axis?: string;
  value: number | undefined;
  onChange: (value: number | undefined) => void;
  min?: number;
  max?: number;
  step?: number;
  decimals?: number;
  suffix?: string;
  /** Empty input means "automatic" (undefined). */
  nullable?: boolean;
  placeholder?: string;
  disabled?: boolean;
  description?: ReactNode;
  ariaLabel?: string;
}

/** Number input that tolerates transient empty/partial text while typing. */
export function NumField({ label, axis, value, onChange, min, max, step = 1, decimals = 2, suffix, nullable, placeholder, disabled, description, ariaLabel }: NumFieldProps) {
  const [draft, setDraft] = useState<number | string>(value ?? '');
  const [focused, setFocused] = useState(false);

  useEffect(() => {
    if (!focused) setDraft(value ?? '');
  }, [value, focused]);

  return (
    <NumberInput
      size="xs"
      label={label}
      description={description}
      aria-label={ariaLabel ?? (typeof label === 'string' ? label : axis)}
      leftSection={axis ? <span className={classes.axisLabel}>{axis}</span> : undefined}
      leftSectionWidth={axis ? 26 : undefined}
      value={draft}
      min={min}
      max={max}
      step={step}
      decimalScale={decimals}
      suffix={suffix}
      placeholder={placeholder}
      disabled={disabled}
      clampBehavior="blur"
      onFocus={() => setFocused(true)}
      onBlur={() => {
        setFocused(false);
        if (draft === '' && !nullable) setDraft(value ?? '');
      }}
      onChange={(v) => {
        setDraft(v);
        if (v === '' || v === '-') {
          if (nullable) onChange(undefined);
          return;
        }
        const n = typeof v === 'number' ? v : Number(v);
        if (Number.isFinite(n)) onChange(n);
      }}
    />
  );
}

export function SwitchField({ label, checked, onChange, description, disabled }: { label: ReactNode; checked: boolean; onChange: (v: boolean) => void; description?: ReactNode; disabled?: boolean }) {
  return (
    <Switch
      size="xs"
      label={label}
      description={description}
      checked={checked}
      disabled={disabled}
      onChange={(e) => onChange(e.currentTarget.checked)}
      styles={{ label: { fontSize: 'var(--mantine-font-size-sm)' }, body: { alignItems: 'center' } }}
    />
  );
}

export interface SegmentOption<T extends string> {
  value: T;
  label: string;
  icon?: ComponentType<IconProps>;
}

export function SegmentField<T extends string>({ label, value, onChange, data, disabled }: { label?: ReactNode; value: T; onChange: (v: T) => void; data: SegmentOption<T>[]; disabled?: boolean }) {
  const control = (
    <SegmentedControl
      size="xs"
      fullWidth
      value={value}
      disabled={disabled}
      onChange={(v) => onChange(v as T)}
      data={data.map((o) => ({
        value: o.value,
        label: o.icon ? (
          <Tooltip label={o.label} openDelay={350}>
            <Center>
              <o.icon size={15} stroke={1.8} />
              <VisuallyHidden>{o.label}</VisuallyHidden>
            </Center>
          </Tooltip>
        ) : (
          o.label
        ),
      }))}
    />
  );
  if (!label) return control;
  return (
    <Input.Wrapper label={label} size="xs">
      <div style={{ marginTop: 2 }}>{control}</div>
    </Input.Wrapper>
  );
}

export function SelectField<T extends string>({ label, value, onChange, data, disabled, placeholder }: { label?: ReactNode; value: T | undefined; onChange: (v: T) => void; data: { value: T; label: string }[] | { group: string; items: { value: T; label: string }[] }[]; disabled?: boolean; placeholder?: string }) {
  return (
    <Select
      size="xs"
      label={label}
      value={value ?? null}
      allowDeselect={false}
      disabled={disabled}
      placeholder={placeholder}
      data={data}
      onChange={(v) => v !== null && onChange(v as T)}
      comboboxProps={{ withinPortal: true }}
      maxDropdownHeight={280}
    />
  );
}

export function SliderField({ label, value, onChange, min, max, step = 1, format, disabled, marks }: { label: ReactNode; value: number; onChange: (v: number) => void; min: number; max: number; step?: number; format?: (v: number) => string; disabled?: boolean; marks?: { value: number; label?: ReactNode }[] }) {
  return (
    <Input.Wrapper
      size="xs"
      label={
        <Group justify="space-between" gap="xs" w="100%" wrap="nowrap">
          <span>{label}</span>
          <Text span size="xs" ff="monospace" c="dimmed">
            {format ? format(value) : value}
          </Text>
        </Group>
      }
      styles={{ label: { width: '100%' } }}
    >
      <Slider
        size="sm"
        mt={4}
        mb={marks ? 14 : 4}
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={onChange}
        disabled={disabled}
        label={format}
        marks={marks}
        aria-label={typeof label === 'string' ? label : undefined}
      />
    </Input.Wrapper>
  );
}
