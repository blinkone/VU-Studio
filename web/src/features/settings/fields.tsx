import { useEffect, useRef, useState } from 'react';
import { NumberInput, TextInput, type NumberInputProps, type TextInputProps } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { toHm, toTimeOnly } from './time';

// Small form building blocks shared by the settings and profiles features.

interface TimeFieldProps extends Omit<TextInputProps, 'value' | 'onChange' | 'type'> {
  /** "HH:mm:ss" (or "HH:mm"). */
  value: string;
  /** Always called with "HH:mm:ss". */
  onChange: (value: string) => void;
}

/**
 * Time-of-day input. A native `type="time"` field is used on purpose: it gives every platform its
 * own accessible picker (including the phone wheel) and needs no extra dependency.
 */
export function TimeField({ value, onChange, ...props }: TimeFieldProps) {
  return (
    <TextInput
      {...props}
      type="time"
      step={60}
      value={toHm(value)}
      onChange={(event) => {
        const next = event.currentTarget.value;
        // The browser reports '' while the user is half-way through typing a time.
        if (next) onChange(toTimeOnly(next));
      }}
    />
  );
}

interface NumFieldProps extends Omit<NumberInputProps, 'value' | 'onChange' | 'defaultValue'> {
  value: number;
  /** Called with every valid value (finite and within min/max), while typing. */
  onCommit: (value: number) => void;
}

/**
 * Number input that keeps its own text while the user types, so clearing the field to type a new
 * number does not snap back. Only valid values reach the configuration draft.
 */
export function NumField({ value, onCommit, min, max, error, onFocus, onBlur, ...props }: NumFieldProps) {
  const { t } = useTranslation('settings');
  const [text, setText] = useState<number | string>(value);
  const editing = useRef(false);
  const latest = useRef({ value, text });
  latest.current = { value, text };

  useEffect(() => {
    if (!editing.current) setText(value);
  }, [value]);

  const inRange = (n: number) => Number.isFinite(n) && (min === undefined || n >= Number(min)) && (max === undefined || n <= Number(max));

  let message = error;
  if (!message && text === '') {
    message = t('validation.required');
  } else if (!message && typeof text === 'number' && !inRange(text)) {
    message =
      min !== undefined && max !== undefined
        ? t('validation.range', { min, max })
        : min !== undefined
          ? t('validation.min', { min })
          : t('validation.max', { max });
  }

  return (
    <NumberInput
      {...props}
      min={min}
      max={max}
      value={text}
      error={message}
      onFocus={(event) => {
        editing.current = true;
        onFocus?.(event);
      }}
      onBlur={(event) => {
        editing.current = false;
        const current = latest.current.text;
        if (typeof current !== 'number' || !inRange(current)) setText(latest.current.value);
        onBlur?.(event);
      }}
      onChange={(next) => {
        setText(next);
        if (typeof next === 'number' && inRange(next)) onCommit(next);
      }}
    />
  );
}
