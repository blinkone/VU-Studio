import { useEffect, useRef, useState } from 'react';
import { ActionIcon, Tooltip, type ActionIconProps } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconCheck, IconCopy } from '@tabler/icons-react';

/**
 * Copies text to the clipboard. `navigator.clipboard` only exists in secure contexts (https or
 * localhost), but this UI is also opened over plain http from other devices on the LAN, so fall back
 * to the legacy `execCommand` path.
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    // fall through to the legacy path
  }
  try {
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    Object.assign(area.style, { position: 'fixed', top: '0', left: '0', opacity: '0' });
    document.body.appendChild(area);
    area.select();
    const ok = document.execCommand('copy');
    area.remove();
    return ok;
  } catch {
    return false;
  }
}

interface CopyActionProps extends Omit<ActionIconProps, 'onClick' | 'children'> {
  /** Text copied on click. */
  value: string;
  /** Accessible name and tooltip, e.g. "Copy token". */
  label: string;
}

/** Icon button that copies `value` and briefly shows a check mark. */
export function CopyAction({ value, label, size = 'md', variant = 'subtle', color = 'gray', ...props }: CopyActionProps) {
  const { t } = useTranslation('settings');
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);

  const copy = async () => {
    setState((await copyText(value)) ? 'copied' : 'failed');
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setState('idle'), 1800);
  };

  return (
    <Tooltip label={state === 'copied' ? t('copied') : state === 'failed' ? t('copyFailed') : label} opened={state === 'idle' ? undefined : true}>
      <ActionIcon
        {...props}
        size={size}
        variant={variant}
        color={state === 'copied' ? 'teal' : state === 'failed' ? 'red' : color}
        onClick={() => void copy()}
        aria-label={label}
      >
        {state === 'copied' ? <IconCheck size={16} /> : <IconCopy size={16} />}
      </ActionIcon>
    </Tooltip>
  );
}
