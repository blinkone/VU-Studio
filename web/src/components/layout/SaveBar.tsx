import { Affix, Alert, Button, Group, Paper, Text, Transition } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useTranslation } from 'react-i18next';
import { IconDeviceFloppy } from '@tabler/icons-react';
import { useConfigDraft } from '@/state/configDraft';

/** Floating bar shown while the configuration draft has unsaved changes. */
export function SaveBar() {
  const { t } = useTranslation();
  const { dirty, saving, errors, save, reset, load } = useConfigDraft();

  const onSave = async () => {
    if (await save()) {
      notifications.show({ message: t('actions.save') + ' ✓', color: 'teal' });
    }
  };

  return (
    <Affix position={{ bottom: 20, left: '50%' }} style={{ transform: 'translateX(-50%)' }} zIndex={200}>
      <Transition transition="slide-up" mounted={dirty}>
        {(styles) => (
          <Paper style={styles} shadow="xl" p="sm" withBorder w={{ base: 340, sm: 520 }}>
            <Group justify="space-between" wrap="nowrap">
              <Text size="sm" fw={600}>
                {t('unsaved.title')}
              </Text>
              <Group gap="xs" wrap="nowrap">
                <Button variant="default" size="xs" onClick={reset} disabled={saving}>
                  {t('actions.discard')}
                </Button>
                <Button size="xs" leftSection={<IconDeviceFloppy size={16} />} loading={saving} onClick={() => void onSave()}>
                  {t('actions.save')}
                </Button>
              </Group>
            </Group>
            {errors.length > 0 && (
              <Alert mt="sm" color="red" variant="light" p="xs">
                {errors[0] === 'conflict' ? (
                  <Group justify="space-between">
                    <Text size="sm">{t('unsaved.conflict')}</Text>
                    <Button size="compact-xs" variant="light" onClick={() => void load()}>
                      {t('actions.reset')}
                    </Button>
                  </Group>
                ) : (
                  errors.map((e) => (
                    <Text key={e} size="sm">
                      {e}
                    </Text>
                  ))
                )}
              </Alert>
            )}
          </Paper>
        )}
      </Transition>
    </Affix>
  );
}
