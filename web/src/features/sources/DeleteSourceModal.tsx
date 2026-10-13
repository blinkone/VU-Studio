import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { IconAlertTriangle, IconInfoCircle } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { ProviderInstanceConfig } from '@/api/types';
import type { SourceUsage } from './usage';

interface DeleteSourceModalProps {
  /** The instance to delete; kept by the owner until the closing animation is over. */
  instance: ProviderInstanceConfig | null;
  /** Display name (instance label or type name). */
  name: string;
  /** Created again by the service at every start (descriptor.autoCreate). */
  autoCreated: boolean;
  usage: SourceUsage | null;
  opened: boolean;
  onClose: () => void;
  onExited: () => void;
  onConfirm: () => void;
  /** Offered instead of deleting an auto-created source. */
  onDisable: () => void;
}

export function DeleteSourceModal({ instance, name, autoCreated, usage, opened, onClose, onExited, onConfirm, onDisable }: DeleteSourceModalProps) {
  const { t } = useTranslation('sources');
  const { t: tc } = useTranslation('common');

  return (
    <Modal opened={opened} onClose={onClose} onExitTransitionEnd={onExited} title={t('delete.title', { name })} centered radius="lg" size="md">
      <Stack gap="md">
        <Text size="sm">{t('delete.body', { id: instance?.id ?? '' })}</Text>

        {usage && (usage.pages > 0 || usage.conditions > 0) && (
          <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={18} />} title={t('delete.usageTitle')}>
            <Stack gap={2}>
              {usage.pages > 0 && <Text size="sm">{t('delete.usagePages', { count: usage.pages })}</Text>}
              {usage.conditions > 0 && <Text size="sm">{t('delete.usageConditions', { count: usage.conditions })}</Text>}
              <Text size="sm">{t('delete.usageHint')}</Text>
            </Stack>
          </Alert>
        )}

        {autoCreated && (
          <Alert color="blue" variant="light" icon={<IconInfoCircle size={18} />}>
            {t('delete.autoCreated')}
          </Alert>
        )}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            {tc('actions.cancel')}
          </Button>
          {autoCreated && (
            <Button variant="light" onClick={onDisable}>
              {t('delete.disableInstead')}
            </Button>
          )}
          <Button color="red" onClick={onConfirm}>
            {t('delete.confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
