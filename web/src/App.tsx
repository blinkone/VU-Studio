import { useEffect } from 'react';
import { AppShell, Burger, Group, NavLink, ScrollArea, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { QueryClient } from '@tanstack/react-query';
import { NavLink as RouterLink, Route, Routes, useLocation } from 'react-router';
import { useTranslation } from 'react-i18next';
import {
  IconChartLine,
  IconGauge,
  IconListSearch,
  IconPlugConnected,
  IconSettings,
  IconStack2,
} from '@tabler/icons-react';
import { connectLive, useLive } from '@/api/live';
import { refreshIfClean, useConfigDraft } from '@/state/configDraft';
import { HeaderBar } from '@/components/layout/HeaderBar';
import { SaveBar } from '@/components/layout/SaveBar';
import { Logo } from '@/components/layout/Logo';
import { DashboardPage } from '@/pages/DashboardPage';
import { DialEditorPage } from '@/pages/DialEditorPage';
import { ProfilesPage } from '@/pages/ProfilesPage';
import { SourcesPage } from '@/pages/SourcesPage';
import { SensorsPage } from '@/pages/SensorsPage';
import { HistoryPage } from '@/pages/HistoryPage';
import { SettingsPage } from '@/pages/SettingsPage';

const navItems = [
  { to: '/', key: 'dashboard', icon: IconGauge },
  { to: '/profiles', key: 'profiles', icon: IconStack2 },
  { to: '/sources', key: 'sources', icon: IconPlugConnected },
  { to: '/sensors', key: 'sensors', icon: IconListSearch },
  { to: '/history', key: 'history', icon: IconChartLine },
  { to: '/settings', key: 'settings', icon: IconSettings },
] as const;

export function App({ queryClient }: { queryClient: QueryClient }) {
  const { t } = useTranslation();
  const [opened, { toggle, close }] = useDisclosure();
  const location = useLocation();
  const configRevision = useLive((s) => s.configRevision);
  const lastNotification = useLive((s) => s.lastNotification);
  const load = useConfigDraft((s) => s.load);

  useEffect(() => {
    connectLive(queryClient);
    void load();
  }, [queryClient, load]);

  useEffect(() => {
    void refreshIfClean(configRevision);
  }, [configRevision]);

  useEffect(() => {
    if (lastNotification) {
      notifications.show({ title: lastNotification.title, message: lastNotification.message, color: 'red' });
    }
  }, [lastNotification]);

  useEffect(close, [location.pathname, close]);

  return (
    <AppShell
      header={{ height: 60 }}
      navbar={{ width: 230, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="lg"
    >
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <Logo />
          </Group>
          <HeaderBar />
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="sm">
        <AppShell.Section grow component={ScrollArea}>
          {navItems.map(({ to, key, icon: Icon }) => (
            <NavLink
              key={to}
              component={RouterLink}
              to={to}
              end={to === '/'}
              label={t(`nav.${key}`)}
              leftSection={<Icon size={20} stroke={1.6} />}
              active={to === '/' ? location.pathname === '/' || location.pathname.startsWith('/dials') : location.pathname.startsWith(to)}
              styles={{ root: { borderRadius: 'var(--mantine-radius-md)' }, label: { fontWeight: 550 } }}
            />
          ))}
        </AppShell.Section>
        <AppShell.Section>
          <Text size="xs" c="dimmed" ta="center">
            {t('appName')} · GPL-3.0
          </Text>
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main>
        <Routes>
          <Route path="/" element={<DashboardPage />} />
          <Route path="/dials/:slot/*" element={<DialEditorPage />} />
          <Route path="/profiles/*" element={<ProfilesPage />} />
          <Route path="/sources/*" element={<SourcesPage />} />
          <Route path="/sensors" element={<SensorsPage />} />
          <Route path="/history" element={<HistoryPage />} />
          <Route path="/settings/*" element={<SettingsPage />} />
          <Route path="*" element={<DashboardPage />} />
        </Routes>
        <SaveBar />
      </AppShell.Main>
    </AppShell>
  );
}
