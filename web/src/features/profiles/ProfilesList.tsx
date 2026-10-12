import { useState } from 'react';
import { Alert, Button, Center, Group, Loader, Menu, Modal, Paper, Stack, Text, Title } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { useNavigate } from 'react-router';
import { useTranslation } from 'react-i18next';
import { IconArrowBackUp, IconBolt, IconFilePlus, IconHandClick, IconPlus } from '@tabler/icons-react';
import { api } from '@/api/client';
import { useLive } from '@/api/live';
import type { Profile } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';
import { runAction } from '@/features/settings/actions';
import { ProfileCard } from './ProfileCard';
import { ProfileIcon } from './ProfileIcon';
import {
  blankProfile,
  copyName,
  duplicateProfile,
  findDefaultProfile,
  findShadowingProfile,
  moveProfile,
  sortProfiles,
} from './profileModel';

/** `/profiles` — all profiles ordered by priority, with the one in use highlighted. */
export function ProfilesList() {
  const { t } = useTranslation('profiles');
  const { t: tc } = useTranslation('common');
  const navigate = useNavigate();
  const draft = useConfigDraft((s) => s.draft);
  const base = useConfigDraft((s) => s.base);
  const update = useConfigDraft((s) => s.update);
  const engine = useLive((s) => s.state?.engine);

  const [busy, setBusy] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<Profile | null>(null);
  const [confirmOpen, confirm] = useDisclosure(false);

  if (!draft) {
    return (
      <Center py="xl">
        <Loader />
      </Center>
    );
  }

  const profiles = draft.profiles;
  const sorted = sortProfiles(profiles);
  const manualId = engine?.manualProfileId;
  const manualProfile = manualId ? profiles.find((p) => p.id === manualId) : undefined;
  const activeProfile = engine?.activeProfileId ? profiles.find((p) => p.id === engine.activeProfileId) : undefined;

  const setActive = async (profileId: string | null) => {
    setBusy(profileId ?? '__auto__');
    await runAction(() => api.post('/api/engine/profile', { profileId }), { error: t('errors.activate') });
    setBusy(null);
  };

  const createBlank = () => {
    const profile = blankProfile(profiles, t('newName'));
    update((d) => {
      d.profiles.push(profile);
    });
    void navigate(`/profiles/${profile.id}`);
  };

  const createCopy = (source: Profile) => {
    const copy = duplicateProfile(
      source,
      profiles,
      copyName(source.name, profiles.map((p) => p.name), t('copySuffix')),
    );
    update((d) => {
      d.profiles.push(copy);
    });
    void navigate(`/profiles/${copy.id}`);
  };

  const askDelete = (profile: Profile) => {
    setPendingDelete(profile);
    confirm.open();
  };

  const doDelete = () => {
    if (!pendingDelete || profiles.length <= 1) return;
    const id = pendingDelete.id;
    update((d) => {
      d.profiles = d.profiles.filter((p) => p.id !== id);
    });
    // A profile that was forced manually no longer exists: hand control back to the automatic mode.
    if (manualId === id) void setActive(null);
    confirm.close();
  };

  const deletingDefault =
    !!pendingDelete && pendingDelete.activation === 'always' && findDefaultProfile(profiles)?.id === pendingDelete.id;

  return (
    <Stack gap="lg">
      <Group justify="space-between" align="flex-start" gap="md">
        <div style={{ flex: '1 1 320px', minWidth: 0 }}>
          <Title order={2}>{t('title')}</Title>
          <Text c="dimmed" mt={4} maw={680}>
            {t('subtitle')}
          </Text>
        </div>
        <Menu position="bottom-end" withinPortal width={260}>
          <Menu.Target>
            <Button leftSection={<IconPlus size={18} />}>{t('new')}</Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item leftSection={<IconFilePlus size={16} />} onClick={createBlank}>
              {t('newBlank')}
            </Menu.Item>
            <Menu.Divider />
            <Menu.Label>{t('newFromCopy')}</Menu.Label>
            {sorted.map((p) => (
              <Menu.Item key={p.id} leftSection={<ProfileIcon icon={p.icon} size={16} />} onClick={() => createCopy(p)}>
                {p.name}
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
      </Group>

      {engine && (
        <Paper withBorder p="sm" px="md">
          <Group justify="space-between" wrap="wrap" gap="sm">
            <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
              {manualProfile ? <IconHandClick size={20} aria-hidden /> : <IconBolt size={20} aria-hidden />}
              <Text size="sm" fw={500}>
                {manualProfile
                  ? t('state.manual', { name: manualProfile.name })
                  : activeProfile
                    ? t('state.auto', { name: activeProfile.name })
                    : t('state.none')}
              </Text>
            </Group>
            {manualProfile && (
              <Button
                size="xs"
                variant="light"
                leftSection={<IconArrowBackUp size={15} />}
                loading={busy === '__auto__'}
                onClick={() => void setActive(null)}
              >
                {t('card.backToAuto')}
              </Button>
            )}
          </Group>
        </Paper>
      )}

      {sorted.length === 0 ? (
        <Paper withBorder p="xl" ta="center">
          <Text c="dimmed">{t('empty')}</Text>
        </Paper>
      ) : (
        <Stack gap="md">
          {sorted.map((profile, rank) => {
            const saved = base?.profiles.find((p) => p.id === profile.id);
            return (
              <ProfileCard
                key={profile.id}
                profile={profile}
                shadowedBy={findShadowingProfile(profile, profiles)}
                rank={rank}
                count={sorted.length}
                active={engine?.activeProfileId === profile.id}
                forced={manualId === profile.id}
                canActivate={profile.enabled && saved?.enabled === true}
                canDelete={profiles.length > 1}
                busy={busy === profile.id || (busy === '__auto__' && manualId === profile.id)}
                onToggleEnabled={(enabled) =>
                  update((d) => {
                    const target = d.profiles.find((p) => p.id === profile.id);
                    if (target) target.enabled = enabled;
                  })
                }
                onMove={(direction) => update((d) => moveProfile(d.profiles, profile.id, direction))}
                onActivate={() => void setActive(profile.id)}
                onBackToAuto={() => void setActive(null)}
                onDuplicate={() => createCopy(profile)}
                onDelete={() => askDelete(profile)}
              />
            );
          })}
        </Stack>
      )}

      <Modal opened={confirmOpen} onClose={confirm.close} title={t('delete.title')} centered>
        <Stack>
          <Text size="sm">{t('delete.message', { name: pendingDelete?.name })}</Text>
          {deletingDefault && (
            <Alert color="yellow" variant="light" p="sm">
              <Text size="sm">{t('delete.warnDefault')}</Text>
            </Alert>
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={confirm.close}>
              {tc('actions.cancel')}
            </Button>
            <Button color="red" onClick={doDelete}>
              {t('delete.confirm')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
