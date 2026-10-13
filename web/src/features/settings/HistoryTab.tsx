import { Stack, Switch } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconDatabase } from '@tabler/icons-react';
import { useConfigDraft } from '@/state/configDraft';
import { NumField } from './fields';
import { Section } from './Section';

/** Settings → History: whether to keep per-minute statistics, and for how long. */
export function HistoryTab() {
  const { t } = useTranslation('settings');
  const history = useConfigDraft((s) => s.draft?.history);
  const update = useConfigDraft((s) => s.update);
  if (!history) return null;

  return (
    <Stack gap="lg">
      <Section title={t('history.title')} description={t('history.description')} icon={<IconDatabase size={20} />}>
        <Switch
          checked={history.enabled}
          onChange={(event) => {
            const enabled = event.currentTarget.checked;
            update((d) => {
              d.history.enabled = enabled;
            });
          }}
          label={t('history.enabled')}
          description={t('history.enabledHelp')}
        />
        <NumField
          label={t('history.retention')}
          description={t('history.retentionHelp')}
          maw={260}
          min={1}
          max={3650}
          allowDecimal={false}
          suffix={` ${t('history.days')}`}
          disabled={!history.enabled}
          value={history.retentionDays}
          onCommit={(retentionDays) =>
            update((d) => {
              d.history.retentionDays = retentionDays;
            })
          }
        />
      </Section>
    </Stack>
  );
}
