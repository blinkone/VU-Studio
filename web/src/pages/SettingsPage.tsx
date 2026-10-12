import { useEffect, useRef } from 'react';
import { Box, Center, Loader, ScrollArea, Stack, Tabs, Text, Title } from '@mantine/core';
import { Navigate, useNavigate, useParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { IconCpu, IconInfoCircle, IconMoonStars, IconAdjustments, IconShieldLock, IconDatabase } from '@tabler/icons-react';
import { useConfigDraft } from '@/state/configDraft';
import { AboutTab } from '@/features/settings/AboutTab';
import { AccessTab } from '@/features/settings/AccessTab';
import { GeneralTab } from '@/features/settings/GeneralTab';
import { HardwareTab } from '@/features/settings/HardwareTab';
import { HistoryTab } from '@/features/settings/HistoryTab';
import { NightTab } from '@/features/settings/NightTab';

const TABS = [
  { value: 'hardware', icon: IconCpu, Panel: HardwareTab },
  { value: 'night', icon: IconMoonStars, Panel: NightTab },
  { value: 'general', icon: IconAdjustments, Panel: GeneralTab },
  { value: 'access', icon: IconShieldLock, Panel: AccessTab },
  { value: 'history', icon: IconDatabase, Panel: HistoryTab },
  { value: 'about', icon: IconInfoCircle, Panel: AboutTab },
] as const;

/** `/settings/:tab` — one deep-linkable sub-route per tab. */
export function SettingsPage() {
  const { t } = useTranslation('settings');
  const navigate = useNavigate();
  const segment = useParams()['*']?.split('/')[0] ?? '';
  const current = TABS.find((x) => x.value === segment);
  const draft = useConfigDraft((s) => s.draft);
  const listRef = useRef<HTMLDivElement>(null);

  // On narrow screens the tab bar scrolls; keep the active tab in view.
  useEffect(() => {
    listRef.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ inline: 'center', block: 'nearest' });
  }, [current?.value]);

  if (!current) return <Navigate to="/settings/hardware" replace />;

  return (
    <Box maw={1100}>
      <Stack gap="lg">
        <div>
          <Title order={2}>{t('title')}</Title>
          <Text c="dimmed" mt={4}>
            {t('subtitle')}
          </Text>
        </div>

        <Tabs value={current.value} onChange={(value) => value && void navigate(`/settings/${value}`)} keepMounted={false}>
          <ScrollArea type="never" offsetScrollbars={false}>
            <Tabs.List ref={listRef} style={{ flexWrap: 'nowrap' }}>
              {TABS.map(({ value, icon: Icon }) => (
                <Tabs.Tab key={value} value={value} leftSection={<Icon size={17} stroke={1.7} />} style={{ whiteSpace: 'nowrap' }}>
                  {t(`tabs.${value}`)}
                </Tabs.Tab>
              ))}
            </Tabs.List>
          </ScrollArea>

          {TABS.map(({ value, Panel }) => (
            <Tabs.Panel key={value} value={value} pt="lg">
              {draft ? (
                <Panel />
              ) : (
                <Center py="xl">
                  <Loader />
                </Center>
              )}
            </Tabs.Panel>
          ))}
        </Tabs>
      </Stack>
    </Box>
  );
}
