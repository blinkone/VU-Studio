import type { ReactNode } from 'react';
import { Group, Radio, SimpleGrid, type StyleProp } from '@mantine/core';
import classes from './editor.module.css';

export interface OptionCard<T extends string> {
  value: T;
  label: ReactNode;
  description?: ReactNode;
  icon?: ReactNode;
  disabled?: boolean;
}

interface OptionCardsProps<T extends string> {
  label?: ReactNode;
  description?: ReactNode;
  value: T;
  onChange: (value: T) => void;
  options: OptionCard<T>[];
  cols?: StyleProp<number>;
}

/** A radio group shown as a grid of cards (arrow keys move between options). */
export function OptionCards<T extends string>({ label, description, value, onChange, options, cols }: OptionCardsProps<T>) {
  return (
    <Radio.Group label={label} description={description} value={value} onChange={(v) => onChange(v as T)}>
      <SimpleGrid cols={cols ?? { base: 2, sm: Math.min(options.length, 4), lg: options.length }} spacing="xs" mt={label || description ? 'xs' : 0}>
        {options.map((option) => (
          <Radio.Card key={option.value} value={option.value} className={classes.option} disabled={option.disabled}>
            <Group gap={8} wrap="nowrap" align="center">
              {option.icon && <span className={classes.optionIcon}>{option.icon}</span>}
              <div className={classes.optionLabel}>{option.label}</div>
            </Group>
            {option.description && <div className={classes.optionDescription}>{option.description}</div>}
          </Radio.Card>
        ))}
      </SimpleGrid>
    </Radio.Group>
  );
}
