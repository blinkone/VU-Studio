import { useMemo, useState } from 'react';
import { Accordion, Stack, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import type { SettingField } from '@/api/types';
import { SettingsForm } from '@/components/settings/SettingsForm';
import { sameJson, splitFields, type SettingsValues } from './settings';

interface SettingsSectionsProps {
  fields: SettingField[];
  value: SettingsValues;
  onChange: (next: SettingsValues) => void;
  disabled?: boolean;
}

/**
 * A provider's settings form. Long schemas keep the essentials on top and fold the tuning knobs
 * (poll intervals, switches…) into a collapsible section; SettingsForm renders each part, tables included.
 */
export function SettingsSections({ fields, value, onChange, disabled }: SettingsSectionsProps) {
  const { t } = useTranslation('sources');
  const { main, more } = useMemo(() => splitFields(fields), [fields]);

  // Start expanded when the instance already deviates from the defaults in the folded part.
  // Decided once, at mount: later edits must not re-open or re-close the panel under the user's hands.
  const [startOpen] = useState(() => more.some((field) => field.key in value && !sameJson(value[field.key], field.default)));

  if (fields.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        {t('editor.noSettings')}
      </Text>
    );
  }

  return (
    <Stack gap="md">
      <SettingsForm fields={main} value={value} onChange={onChange} disabled={disabled} />
      {more.length > 0 && (
        <Accordion variant="contained" radius="md" chevronPosition="left" defaultValue={startOpen ? 'more' : null}>
          <Accordion.Item value="more">
            <Accordion.Control>{t('editor.moreOptions', { count: more.length })}</Accordion.Control>
            <Accordion.Panel>
              <SettingsForm fields={more} value={value} onChange={onChange} disabled={disabled} />
            </Accordion.Panel>
          </Accordion.Item>
        </Accordion>
      )}
    </Stack>
  );
}
