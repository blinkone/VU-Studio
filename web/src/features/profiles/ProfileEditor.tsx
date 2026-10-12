import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Grid,
  Group,
  Input,
  Loader,
  SegmentedControl,
  Stack,
  Switch,
  Text,
  TextInput,
  ThemeIcon,
  Title,
} from '@mantine/core';
import { Link, useParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { IconAlertTriangle, IconArrowBackUp, IconArrowLeft, IconInfoCircle, IconPlayerPlay } from '@tabler/icons-react';
import { api } from '@/api/client';
import { useLive } from '@/api/live';
import type { Profile, ProfileActivation } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';
import { NumField } from '@/features/settings/fields';
import { runAction } from '@/features/settings/actions';
import { ConditionsEditor } from './ConditionsEditor';
import { DialsOverview } from './DialsOverview';
import { IconSelect } from './IconSelect';
import { ProfileIcon } from './ProfileIcon';
import { ACTIVATION_ICONS } from './ProfileCard';
import { findShadowingProfile } from './profileModel';

const ACTIVATIONS: readonly ProfileActivation[] = ['auto', 'always', 'manual'];

/** `/profiles/:id` — everything about one profile. Edits go to the shared configuration draft. */
export function ProfileEditor() {
  const { id } = useParams();
  const { t } = useTranslation('profiles');
  const draft = useConfigDraft((s) => s.draft);
  const base = useConfigDraft((s) => s.base);
  const update = useConfigDraft((s) => s.update);
  const engine = useLive((s) => s.state?.engine);
  const [busy, setBusy] = useState(false);

  if (!draft) {
    return (
      <Center py="xl">
        <Loader />
      </Center>
    );
  }

  const profile = draft.profiles.find((p) => p.id === id);
  if (!profile) {
    return (
      <Stack align="center" py="xl" gap="md">
        <ThemeIcon size={56} radius="xl" variant="light" color="gray">
          <IconAlertTriangle size={30} />
        </ThemeIcon>
        <Title order={3}>{t('editor.notFound')}</Title>
        <Text c="dimmed" ta="center" maw={420}>
          {t('editor.notFoundHelp')}
        </Text>
        <Button component={Link} to="/profiles" leftSection={<IconArrowLeft size={16} />}>
          {t('editor.back')}
        </Button>
      </Stack>
    );
  }

  const edit = (recipe: (draftProfile: Profile) => void) =>
    update((d) => {
      const target = d.profiles.find((p) => p.id === profile.id);
      if (target) recipe(target);
    });

  const active = engine?.activeProfileId === profile.id;
  const forced = engine?.manualProfileId === profile.id;
  const saved = base?.profiles.find((p) => p.id === profile.id);
  const canActivate = profile.enabled && saved?.enabled === true;
  const shadowedBy = findShadowingProfile(profile, draft.profiles);
  const nameMissing = profile.name.trim() === '';

  const activate = async (profileId: string | null) => {
    setBusy(true);
    await runAction(() => api.post('/api/engine/profile', { profileId }), { error: t('errors.activate') });
    setBusy(false);
  };

  return (
    <Stack gap="lg">
      <div>
        <Button
          component={Link}
          to="/profiles"
          variant="subtle"
          color="gray"
          size="compact-sm"
          leftSection={<IconArrowLeft size={16} />}
          ml={-8}
        >
          {t('editor.back')}
        </Button>
        <Group justify="space-between" align="center" mt="xs" gap="md">
          <Group gap="md" wrap="nowrap" style={{ minWidth: 0 }}>
            <ThemeIcon size={48} radius="md" variant={active ? 'filled' : 'light'} color={active ? undefined : 'gray'}>
              <ProfileIcon icon={profile.icon} size={28} />
            </ThemeIcon>
            <div style={{ minWidth: 0 }}>
              <Title order={2} lh={1.2} style={{ overflowWrap: 'anywhere' }}>
                {profile.name.trim() || t('newName')}
              </Title>
              <Group gap={6} mt={6}>
                {active && <Badge variant="filled">{t('badges.active')}</Badge>}
                {forced && (
                  <Badge variant="light" color="grape">
                    {t('badges.forced')}
                  </Badge>
                )}
                {!profile.enabled && (
                  <Badge variant="outline" color="gray">
                    {t('badges.disabled')}
                  </Badge>
                )}
              </Group>
            </div>
          </Group>
          {forced ? (
            <Button variant="light" leftSection={<IconArrowBackUp size={16} />} loading={busy} onClick={() => void activate(null)}>
              {t('card.backToAuto')}
            </Button>
          ) : (
            <Button
              variant="light"
              leftSection={<IconPlayerPlay size={16} />}
              loading={busy}
              disabled={!canActivate}
              title={canActivate ? undefined : profile.enabled ? t('card.saveFirst') : t('card.enableFirst')}
              onClick={() => void activate(profile.id)}
            >
              {t('card.activate')}
            </Button>
          )}
        </Group>
      </div>

      <Grid gutter="lg">
        <Grid.Col span={{ base: 12, lg: 7 }}>
          <Stack gap="lg">
            <Card padding="lg">
              <Stack gap="md">
                <Title order={4}>{t('editor.general')}</Title>

                <TextInput
                  label={t('editor.name')}
                  required
                  maxLength={60}
                  value={profile.name}
                  error={nameMissing ? t('editor.nameRequired') : undefined}
                  onChange={(event) => {
                    const name = event.currentTarget.value;
                    edit((p) => {
                      p.name = name;
                    });
                  }}
                />

                <IconSelect
                  label={t('editor.icon')}
                  description={t('editor.iconHelp')}
                  value={profile.icon}
                  onChange={(icon) =>
                    edit((p) => {
                      p.icon = icon;
                    })
                  }
                />

                <Input.Wrapper label={t('editor.activation')} description={t(`activation.help.${profile.activation}`)}>
                  <SegmentedControl
                    mt={8}
                    fullWidth
                    orientation="horizontal"
                    value={profile.activation}
                    onChange={(value) =>
                      edit((p) => {
                        p.activation = value as ProfileActivation;
                      })
                    }
                    data={ACTIVATIONS.map((value) => {
                      const Glyph = ACTIVATION_ICONS[value];
                      return {
                        value,
                        label: (
                          <Center style={{ gap: 6 }}>
                            <Glyph size={15} stroke={1.8} aria-hidden />
                            <span>{t(`activation.${value}`)}</span>
                          </Center>
                        ),
                      };
                    })}
                  />
                </Input.Wrapper>

                <Group grow align="flex-start" gap="md">
                  <NumField
                    label={t('editor.priority')}
                    description={t('editor.priorityHelp')}
                    value={profile.priority}
                    min={0}
                    max={999}
                    allowDecimal={false}
                    onCommit={(priority) =>
                      edit((p) => {
                        p.priority = priority;
                      })
                    }
                  />
                  <Switch
                    mt={26}
                    checked={profile.enabled}
                    onChange={(event) => {
                      const enabled = event.currentTarget.checked;
                      edit((p) => {
                        p.enabled = enabled;
                      });
                    }}
                    label={t('editor.enabled')}
                    description={t('editor.enabledHelp')}
                  />
                </Group>

                {shadowedBy && (
                  <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={18} />} p="sm">
                    <Text size="sm">{t('card.shadowed', { name: shadowedBy.name })}</Text>
                  </Alert>
                )}
              </Stack>
            </Card>

            <Card padding="lg">
              <Stack gap="md">
                <Title order={4}>{t('editor.conditions')}</Title>
                {profile.activation === 'auto' ? (
                  <ConditionsEditor profile={profile} edit={edit} />
                ) : (
                  <Alert variant="light" color="gray" icon={<IconInfoCircle size={18} />} p="sm">
                    <Text size="sm">{t(`editor.conditionsIgnored.${profile.activation}`)}</Text>
                  </Alert>
                )}
              </Stack>
            </Card>
          </Stack>
        </Grid.Col>

        <Grid.Col span={{ base: 12, lg: 5 }}>
          <Card padding="lg">
            <Stack gap="md">
              <div>
                <Title order={4}>{t('editor.dials')}</Title>
                <Text size="sm" c="dimmed" mt={2}>
                  {t('editor.dialsHelp')}
                </Text>
              </div>
              <DialsOverview
                profile={profile}
                profiles={draft.profiles}
                dials={draft.hardware.dials}
                onInheritChange={(inherit) =>
                  edit((p) => {
                    p.inheritUnassigned = inherit;
                  })
                }
              />
            </Stack>
          </Card>
        </Grid.Col>
      </Grid>
    </Stack>
  );
}
