import { Alert, Divider, Group, NumberInput, SegmentedControl, Slider, Stack, Switch, Text } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { FaceConfig, ThemeInfo } from '@/api/types';
import { SettingsForm } from '@/components/settings/SettingsForm';
import type { FaceUpdater } from '../model/history';
import { IconPicker } from './IconPicker';
import { Section } from './fields';
import { TemplateInput } from './TemplateInput';

interface OptionsPanelProps {
  face: FaceConfig;
  theme: ThemeInfo | undefined;
  update: FaceUpdater;
  defaultTitle: string;
  defaultIcon: string | undefined;
}

const REFRESH_PRESETS = [10, 30, 60, 300];

/** Title, icon and display options of the face; theme-only options are hidden for custom layouts. */
export function OptionsPanel({ face, theme, update, defaultTitle, defaultIcon }: OptionsPanelProps) {
  const { t } = useTranslation('face');
  const custom = face.custom !== undefined;
  const set = <K extends keyof FaceConfig>(key: K, value: FaceConfig[K], coalesce?: string) =>
    update((f) => ({ ...f, [key]: value }), coalesce ? { coalesce } : undefined);
  const inverted = custom ? (face.custom?.inverted ?? false) : face.inverted;
  const warnOn = face.warnFrom !== null && face.warnFrom !== undefined;

  return (
    <Stack gap="lg">
      <Section title={t('options.text')}>
        <TemplateInput
          label={t('options.title')}
          placeholder={defaultTitle}
          value={face.title ?? ''}
          exclude={['{title}']}
          onChange={(v) => set('title', v === '' ? undefined : v, 'title')}
        />
        <TemplateInput
          label={t('options.subtitle')}
          placeholder={t('options.subtitlePlaceholder')}
          value={face.subtitle ?? ''}
          exclude={['{subtitle}']}
          onChange={(v) => set('subtitle', v === '' ? undefined : v, 'subtitle')}
        />
        <IconPicker
          label={t('options.icon')}
          value={face.icon}
          automatic={{ value: undefined, label: t('options.iconAuto'), resolved: defaultIcon }}
          onChange={(v) => set('icon', v)}
        />
      </Section>

      <Divider />

      <Section title={t('options.display')}>
        {custom ? (
          <Alert variant="light" color="gray" icon={<IconInfoCircle size={18} />} p="xs">
            <Text size="xs">{t('options.customHint')}</Text>
          </Alert>
        ) : (
          <>
            <Switch label={t('options.showIcon')} checked={face.showIcon} onChange={(e) => set('showIcon', e.currentTarget.checked)} />
            <Switch label={t('options.showValue')} checked={face.showValue} onChange={(e) => set('showValue', e.currentTarget.checked)} />
            <Switch
              label={t('options.showSparkline')}
              description={t('options.showSparklineHint')}
              checked={face.showSparkline}
              onChange={(e) => set('showSparkline', e.currentTarget.checked)}
            />
            <Stack gap={6}>
              <Switch
                label={t('options.warnZone')}
                description={t('options.warnZoneHint')}
                checked={warnOn}
                onChange={(e) => set('warnFrom', e.currentTarget.checked ? 0.85 : null)}
              />
              {warnOn && (
                <Group gap="sm" wrap="nowrap" pl={46}>
                  <Slider
                    style={{ flex: 1 }}
                    min={0.5}
                    max={0.99}
                    step={0.01}
                    value={face.warnFrom ?? 0.85}
                    label={(v) => `${Math.round(v * 100)}%`}
                    onChange={(v) => set('warnFrom', v, 'warnFrom')}
                    aria-label={t('options.warnFrom')}
                    color="red"
                  />
                  <Text size="xs" ff="monospace" c="dimmed" w={36} ta="right">
                    {Math.round((face.warnFrom ?? 0.85) * 100)}%
                  </Text>
                </Group>
              )}
            </Stack>
            <Stack gap={4}>
              <Group justify="space-between">
                <Text size="sm">{t('options.labelCount')}</Text>
                <Text size="xs" ff="monospace" c="dimmed">
                  {face.labelCount}
                </Text>
              </Group>
              <Slider
                min={2}
                max={12}
                step={1}
                value={face.labelCount}
                onChange={(v) => set('labelCount', v, 'labelCount')}
                marks={[{ value: 2 }, { value: 5 }, { value: 8 }, { value: 12 }]}
                aria-label={t('options.labelCount')}
              />
            </Stack>
          </>
        )}
        <Switch
          label={t('options.inverted')}
          description={t('options.invertedHint')}
          checked={inverted}
          onChange={(e) => {
            const checked = e.currentTarget.checked;
            update((f) => (f.custom ? { ...f, custom: { ...f.custom, inverted: checked } } : { ...f, inverted: checked }));
          }}
        />
      </Section>

      <Divider />

      <Section title={t('options.refresh')}>
        <Group gap="xs" wrap="nowrap" align="flex-end">
          <NumberInput
            label={t('options.minRefresh')}
            suffix=" s"
            min={1}
            max={3600}
            w={120}
            value={face.minRefreshSeconds}
            onChange={(v) => typeof v === 'number' && set('minRefreshSeconds', v, 'refresh')}
          />
          <SegmentedControl
            size="xs"
            style={{ flex: 1 }}
            value={REFRESH_PRESETS.includes(face.minRefreshSeconds) ? String(face.minRefreshSeconds) : ''}
            onChange={(v) => set('minRefreshSeconds', Number(v))}
            data={REFRESH_PRESETS.map((s) => ({ value: String(s), label: s >= 60 ? `${s / 60} min` : `${s} s` }))}
            aria-label={t('options.minRefresh')}
          />
        </Group>
        <Text size="xs" c="dimmed" lh={1.45}>
          {t('options.refreshHint')}
        </Text>
      </Section>

      {!custom && theme && theme.options.length > 0 && (
        <>
          <Divider />
          <Section title={t('options.themeOptions')}>
            <SettingsForm
              fields={theme.options}
              value={face.options ?? {}}
              onChange={(next) => update((f) => ({ ...f, options: next }), { coalesce: 'themeOptions' })}
            />
          </Section>
        </>
      )}
    </Stack>
  );
}
