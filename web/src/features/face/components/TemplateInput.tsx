import { useRef, useState, type ReactNode } from 'react';
import { ActionIcon, Divider, Popover, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { IconBraces } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { SensorPicker } from '@/components/SensorPicker';
import classes from '../FaceEditor.module.css';

const TOKENS = ['{value}', '{unit}', '{title}', '{subtitle}', '{min}', '{max}', '{percent}', '{time:HH:mm}', '{date:dd/MM}'] as const;

const tokenKey = (token: string) => token.replace(/[{}]/g, '').split(':')[0];

interface TemplateInputProps {
  value: string;
  onChange: (value: string) => void;
  label?: ReactNode;
  description?: ReactNode;
  placeholder?: string;
  size?: 'xs' | 'sm';
  /** Tokens to leave out of the Insert menu (e.g. {title} inside the title itself). */
  exclude?: string[];
  ariaLabel?: string;
}

/**
 * Text input for face templates with an "Insert" menu for placeholders ({value}, {time:HH:mm},
 * {sensor:<id>}…). Tokens go in at the caret.
 */
export function TemplateInput({ value, onChange, label, description, placeholder, size = 'sm', exclude = [], ariaLabel }: TemplateInputProps) {
  const { t } = useTranslation('face');
  const input = useRef<HTMLInputElement>(null);
  const caret = useRef<{ start: number; end: number } | null>(null);
  const [opened, setOpened] = useState(false);

  const remember = () => {
    const el = input.current;
    if (el) caret.current = { start: el.selectionStart ?? el.value.length, end: el.selectionEnd ?? el.value.length };
  };

  const insert = (token: string) => {
    const at = caret.current ?? { start: value.length, end: value.length };
    const next = value.slice(0, at.start) + token + value.slice(at.end);
    onChange(next);
    setOpened(false);
    const position = at.start + token.length;
    caret.current = { start: position, end: position };
    requestAnimationFrame(() => {
      input.current?.focus();
      input.current?.setSelectionRange(position, position);
    });
  };

  return (
    <TextInput
      ref={input}
      size={size}
      label={label}
      description={description}
      placeholder={placeholder}
      aria-label={ariaLabel}
      value={value}
      onChange={(e) => {
        onChange(e.currentTarget.value);
        remember();
      }}
      onSelect={remember}
      onKeyUp={remember}
      onClick={remember}
      onBlur={remember}
      styles={{ input: { fontFamily: value.includes('{') ? 'var(--mantine-font-family-monospace)' : undefined } }}
      rightSectionWidth={34}
      rightSection={
        <Popover opened={opened} onChange={setOpened} position="bottom-end" width={290} shadow="lg" withinPortal trapFocus>
          <Popover.Target>
            <Tooltip label={t('template.insert')} disabled={opened}>
              <ActionIcon
                variant="subtle"
                size={size === 'xs' ? 'sm' : 'md'}
                aria-label={t('template.insert')}
                aria-haspopup="dialog"
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => setOpened((o) => !o)}
              >
                <IconBraces size={size === 'xs' ? 14 : 16} />
              </ActionIcon>
            </Tooltip>
          </Popover.Target>
          <Popover.Dropdown p={6}>
            <Stack gap={2}>
              <Text className={classes.sectionTitle} px={8} pt={4} pb={2}>
                {t('template.insert')}
              </Text>
              {TOKENS.filter((token) => !exclude.includes(token)).map((token, index) => (
                <button key={token} type="button" className={classes.tokenButton} onClick={() => insert(token)} data-autofocus={index === 0 || undefined}>
                  <Text size="sm">{t(`template.tokens.${tokenKey(token)}`)}</Text>
                  <span className={classes.tokenCode}>{token}</span>
                </button>
              ))}
              <Divider my={4} />
              <Stack gap={4} px={8} pb={6}>
                <Text size="sm">{t('template.tokens.sensor')}</Text>
                <SensorPicker
                  size="xs"
                  value={undefined}
                  placeholder={t('template.pickSensor')}
                  onChange={(id) => id && insert(`{sensor:${id}}`)}
                  comboboxProps={{ withinPortal: false }}
                />
                <Text size="xs" c="dimmed" lh={1.35}>
                  {t('template.formatHint')}
                </Text>
              </Stack>
            </Stack>
          </Popover.Dropdown>
        </Popover>
      }
    />
  );
}
