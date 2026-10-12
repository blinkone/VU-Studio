import { ActionIcon, Badge, Group, Menu, Select, Tooltip, useMantineColorScheme } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconLanguage, IconMoon, IconPlayerPause, IconPlayerPlay, IconSun, IconUsb } from '@tabler/icons-react';
import { api } from '@/api/client';
import { useLive } from '@/api/live';
import { useConfigDraft } from '@/state/configDraft';

/** Hub status, active profile switcher, pause, language and color scheme. */
export function HeaderBar() {
  const { t, i18n } = useTranslation();
  const state = useLive((s) => s.state);
  const status = useLive((s) => s.status);
  const profiles = useConfigDraft((s) => s.base?.profiles) ?? [];
  const { colorScheme, setColorScheme } = useMantineColorScheme();

  const hub = state?.hub;
  const hubColor = status !== 'open' ? 'gray' : hub?.state === 'connected' ? (hub.simulated ? 'grape' : 'teal') : hub?.state === 'error' ? 'red' : 'yellow';
  const hubLabel =
    status !== 'open'
      ? t('status.connecting')
      : hub?.state === 'connected'
        ? hub.simulated
          ? t('status.simulated')
          : hub.port ?? t('status.connected')
        : t(`status.${hub?.state ?? 'disconnected'}`);

  const engine = state?.engine;
  const profileValue = engine?.manualProfileId ?? '__auto__';
  const paused = engine?.paused ?? false;

  return (
    <Group gap="xs" wrap="nowrap">
      <Tooltip label={hub?.message ?? t('hub.title')}>
        <Badge variant="light" color={hubColor} leftSection={<IconUsb size={14} />} visibleFrom="xs">
          {hubLabel}
        </Badge>
      </Tooltip>

      <Select
        size="xs"
        w={190}
        visibleFrom="sm"
        aria-label={t('profile.active')}
        value={profileValue}
        allowDeselect={false}
        data={[
          { value: '__auto__', label: `${t('profile.auto')}${engine?.activeProfileName ? ` · ${engine.activeProfileName}` : ''}` },
          ...profiles.filter((p) => p.enabled).map((p) => ({ value: p.id, label: p.name })),
        ]}
        onChange={(value) => void api.post('/api/engine/profile', { profileId: value === '__auto__' ? null : value })}
      />

      <Tooltip label={paused ? t('profile.paused') : undefined} disabled={!paused}>
        <ActionIcon
          variant={paused ? 'filled' : 'default'}
          size="lg"
          aria-label="pause"
          onClick={() => void api.post('/api/engine/pause', { paused: !paused })}
        >
          {paused ? <IconPlayerPlay size={18} /> : <IconPlayerPause size={18} />}
        </ActionIcon>
      </Tooltip>

      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <ActionIcon variant="default" size="lg" aria-label={t('language.label')}>
            <IconLanguage size={18} />
          </ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item onClick={() => void i18n.changeLanguage('it')}>{t('language.it')}</Menu.Item>
          <Menu.Item onClick={() => void i18n.changeLanguage('en')}>{t('language.en')}</Menu.Item>
        </Menu.Dropdown>
      </Menu>

      <ActionIcon
        variant="default"
        size="lg"
        aria-label="color scheme"
        onClick={() => setColorScheme(colorScheme === 'dark' ? 'light' : 'dark')}
      >
        {colorScheme === 'dark' ? <IconSun size={18} /> : <IconMoon size={18} />}
      </ActionIcon>
    </Group>
  );
}
