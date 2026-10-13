import { Center, Input, SegmentedControl, Stack, Switch, Text, useMantineColorScheme } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconDeviceDesktop, IconLanguage, IconMoon, IconPalette, IconSun, IconTemperature } from '@tabler/icons-react';
import i18n from '@/i18n';
import type { GeneralSettings } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';
import { Section } from './Section';

type Language = GeneralSettings['language'];

/** Where the language detector caches the user's choice (see i18n/index.ts). */
const detectorKey = (): string => {
  const detection = i18n.options.detection as { lookupLocalStorage?: string } | undefined;
  return detection?.lookupLocalStorage ?? 'vustudio.lang';
};

/** Switches the UI language right away. "auto" follows the browser's preferred language. */
function applyLanguage(language: Language) {
  if (language !== 'auto') {
    void i18n.changeLanguage(language);
    return;
  }
  const preferred = (navigator.languages?.length ? navigator.languages : [navigator.language])
    .map((l) => l.toLowerCase())
    .find((l) => l.startsWith('it') || l.startsWith('en'));
  void i18n.changeLanguage(preferred?.startsWith('it') ? 'it' : 'en').then(() => {
    // The detector remembers the last language; forget it so that "auto" keeps following the browser.
    try {
      localStorage.removeItem(detectorKey());
    } catch {
      // storage may be unavailable (private mode); nothing to clean up then
    }
  });
}

/** Settings → General: language, units and appearance. */
export function GeneralTab() {
  const { t, i18n: current } = useTranslation('settings');
  const { t: tc } = useTranslation('common');
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const general = useConfigDraft((s) => s.draft?.general);
  const update = useConfigDraft((s) => s.update);
  if (!general) return null;

  const fahrenheit = general.temperatureUnit.toUpperCase().includes('F');
  const resolved = current.resolvedLanguage === 'it' ? tc('language.it') : tc('language.en');

  return (
    <Stack gap="lg">
      <Section title={t('general.languageTitle')} description={t('general.languageDescription')} icon={<IconLanguage size={20} />}>
        <Input.Wrapper label={t('general.language')} description={t('general.languageAutoNote')}>
          <SegmentedControl
            mt={8}
            value={general.language}
            data={[
              { value: 'auto', label: tc('language.auto') },
              { value: 'en', label: tc('language.en') },
              { value: 'it', label: tc('language.it') },
            ]}
            onChange={(value) => {
              const language = value as Language;
              update((d) => {
                d.general.language = language;
              });
              applyLanguage(language);
            }}
          />
        </Input.Wrapper>
        {general.language === 'auto' && (
          <Text size="xs" c="dimmed" mt={-6}>
            {t('general.languageNow', { language: resolved })}
          </Text>
        )}
      </Section>

      <Section title={t('general.unitsTitle')} description={t('general.unitsDescription')} icon={<IconTemperature size={20} />}>
        <Input.Wrapper label={t('general.temperature')} description={t('general.temperatureHelp')}>
          <SegmentedControl
            mt={8}
            value={fahrenheit ? '°F' : '°C'}
            data={[
              { value: '°C', label: t('general.celsius') },
              { value: '°F', label: t('general.fahrenheit') },
            ]}
            onChange={(value) =>
              update((d) => {
                d.general.temperatureUnit = value;
              })
            }
          />
        </Input.Wrapper>
        <Switch
          checked={general.networkInBits}
          onChange={(event) => {
            const networkInBits = event.currentTarget.checked;
            update((d) => {
              d.general.networkInBits = networkInBits;
            });
          }}
          label={t('general.bits')}
          description={t('general.bitsHelp')}
        />
      </Section>

      <Section title={t('general.appearanceTitle')} description={t('general.appearanceDescription')} icon={<IconPalette size={20} />}>
        <Input.Wrapper label={t('general.colorScheme')} description={t('general.colorSchemeHelp')}>
          <SegmentedControl
            mt={8}
            value={colorScheme}
            onChange={(value) => setColorScheme(value as 'auto' | 'light' | 'dark')}
            data={[
              {
                value: 'auto',
                label: (
                  <Center style={{ gap: 6 }}>
                    <IconDeviceDesktop size={15} aria-hidden />
                    <span>{tc('theme.auto')}</span>
                  </Center>
                ),
              },
              {
                value: 'light',
                label: (
                  <Center style={{ gap: 6 }}>
                    <IconSun size={15} aria-hidden />
                    <span>{tc('theme.light')}</span>
                  </Center>
                ),
              },
              {
                value: 'dark',
                label: (
                  <Center style={{ gap: 6 }}>
                    <IconMoon size={15} aria-hidden />
                    <span>{tc('theme.dark')}</span>
                  </Center>
                ),
              },
            ]}
          />
        </Input.Wrapper>
      </Section>
    </Stack>
  );
}
