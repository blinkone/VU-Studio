import { useCallback, useRef, useState, type ReactNode } from 'react';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';

export interface ConfirmOptions {
  title: string;
  message: ReactNode;
  confirmLabel: string;
  danger?: boolean;
}

/** A small promise-based confirmation dialog: `if (await confirm({...})) …`. Render `dialog` once. */
export function useConfirm(): [ReactNode, (options: ConfirmOptions) => Promise<boolean>] {
  const { t } = useTranslation();
  const [options, setOptions] = useState<ConfirmOptions | null>(null);
  const resolver = useRef<((value: boolean) => void) | null>(null);

  const confirm = useCallback((next: ConfirmOptions) => {
    resolver.current?.(false);
    setOptions(next);
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve;
    });
  }, []);

  const close = (result: boolean) => {
    resolver.current?.(result);
    resolver.current = null;
    setOptions(null);
  };

  const dialog = (
    <Modal opened={options !== null} onClose={() => close(false)} title={<Text fw={650}>{options?.title}</Text>} centered size="sm" radius="lg">
      <Stack gap="lg">
        <Text size="sm" c="dimmed">
          {options?.message}
        </Text>
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={() => close(false)}>
            {t('actions.cancel')}
          </Button>
          <Button color={options?.danger ? 'red' : undefined} onClick={() => close(true)} data-autofocus>
            {options?.confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );

  return [dialog, confirm];
}
