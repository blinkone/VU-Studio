import { Badge, Button, Card, Group, Menu, Stack, Switch, Text, ThemeIcon, Tooltip, ActionIcon } from '@mantine/core';
import { Link } from 'react-router';
import { useTranslation } from 'react-i18next';
import {
  IconAlertTriangle,
  IconArrowBackUp,
  IconArrowDown,
  IconArrowUp,
  IconBolt,
  IconCopyPlus,
  IconDots,
  IconHandClick,
  IconInfinity,
  IconPencil,
  IconPlayerPlay,
  IconTrash,
  type Icon,
} from '@tabler/icons-react';
import type { Profile, ProfileActivation } from '@/api/types';
import { ConditionSummary } from './ConditionSummary';
import { ProfileIcon } from './ProfileIcon';
import classes from './profiles.module.css';

export const ACTIVATION_ICONS: Record<ProfileActivation, Icon> = {
  always: IconInfinity,
  auto: IconBolt,
  manual: IconHandClick,
};

interface ProfileCardProps {
  profile: Profile;
  /** Profile that always outranks this one, so it can never activate on its own. */
  shadowedBy?: Profile;
  rank: number;
  count: number;
  /** The engine is using this profile right now. */
  active: boolean;
  /** The user forced this profile (it stays active until "back to automatic"). */
  forced: boolean;
  /** Activation needs the profile to exist on the service (saved) and to be enabled there. */
  canActivate: boolean;
  canDelete: boolean;
  busy: boolean;
  onToggleEnabled: (enabled: boolean) => void;
  onMove: (direction: 'up' | 'down') => void;
  onActivate: () => void;
  onBackToAuto: () => void;
  onDuplicate: () => void;
  onDelete: () => void;
}

export function ProfileCard({
  profile,
  shadowedBy,
  rank,
  count,
  active,
  forced,
  canActivate,
  canDelete,
  busy,
  onToggleEnabled,
  onMove,
  onActivate,
  onBackToAuto,
  onDuplicate,
  onDelete,
}: ProfileCardProps) {
  const { t } = useTranslation('profiles');
  const { t: tc } = useTranslation('common');
  const ActivationGlyph = ACTIVATION_ICONS[profile.activation];
  const editPath = `/profiles/${encodeURIComponent(profile.id)}`;

  return (
    <Card padding="md" className={`${classes.card} ${active ? classes.activeCard : ''}`} aria-current={active ? 'true' : undefined}>
      <Group align="flex-start" wrap="nowrap" gap="sm">
        <Stack gap={2} className={classes.rail} role="group" aria-label={t('card.priority', { n: profile.priority })}>
          <Tooltip label={t('card.moveUp')}>
            <ActionIcon
              variant="subtle"
              color="gray"
              size="sm"
              disabled={rank === 0}
              onClick={() => onMove('up')}
              aria-label={t('card.moveUp')}
            >
              <IconArrowUp size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('card.priorityHint', { n: profile.priority })}>
            <Text size="xs" fw={700} c="dimmed" ta="center" aria-hidden>
              {profile.priority}
            </Text>
          </Tooltip>
          <Tooltip label={t('card.moveDown')}>
            <ActionIcon
              variant="subtle"
              color="gray"
              size="sm"
              disabled={rank === count - 1}
              onClick={() => onMove('down')}
              aria-label={t('card.moveDown')}
            >
              <IconArrowDown size={16} />
            </ActionIcon>
          </Tooltip>
        </Stack>

        <Stack gap="sm" style={{ flex: 1, minWidth: 0 }}>
          <Group wrap="nowrap" align="flex-start" justify="space-between" gap="sm">
            <Group wrap="nowrap" gap="sm" align="flex-start" style={{ minWidth: 0 }} className={profile.enabled ? undefined : classes.disabledBody}>
              <ThemeIcon size={44} radius="md" variant={active ? 'filled' : 'light'} color={active ? undefined : 'gray'}>
                <ProfileIcon icon={profile.icon} size={26} />
              </ThemeIcon>
              <Stack gap={6} style={{ minWidth: 0 }}>
                <Text component={Link} to={editPath} fw={650} size="lg" lh={1.2} className={classes.title} truncate>
                  {profile.name}
                </Text>
                <Group gap={6}>
                  {active && (
                    <Badge variant="filled" leftSection={<span className={classes.liveDot} aria-hidden />}>
                      {t('badges.active')}
                    </Badge>
                  )}
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
                  <Badge variant="light" color="gray" leftSection={<ActivationGlyph size={13} stroke={1.8} />}>
                    {t(`activation.${profile.activation}`)}
                  </Badge>
                </Group>
              </Stack>
            </Group>
            <Switch
              checked={profile.enabled}
              onChange={(event) => onToggleEnabled(event.currentTarget.checked)}
              aria-label={t('card.enabled', { name: profile.name })}
              mt={4}
            />
          </Group>

          <div className={profile.enabled ? undefined : classes.disabledBody}>
            <ConditionSummary profile={profile} />
          </div>

          {shadowedBy && (
            <Group gap={6} wrap="nowrap" align="flex-start">
              <IconAlertTriangle size={16} color="var(--mantine-color-yellow-6)" style={{ flex: 'none', marginTop: 2 }} aria-hidden />
              <Text size="xs" c="yellow.7">
                {t('card.shadowed', { name: shadowedBy.name })}
              </Text>
            </Group>
          )}

          <Group gap="xs" justify="space-between">
            <Group gap="xs">
              {forced ? (
                <Button size="xs" variant="light" leftSection={<IconArrowBackUp size={15} />} loading={busy} onClick={onBackToAuto}>
                  {t('card.backToAuto')}
                </Button>
              ) : (
                <Tooltip label={profile.enabled ? t('card.saveFirst') : t('card.enableFirst')} disabled={canActivate}>
                  <span>
                    <Button
                      size="xs"
                      variant={active ? 'default' : 'light'}
                      leftSection={<IconPlayerPlay size={15} />}
                      loading={busy}
                      disabled={!canActivate}
                      onClick={onActivate}
                    >
                      {t('card.activate')}
                    </Button>
                  </span>
                </Tooltip>
              )}
              <Button size="xs" variant="default" component={Link} to={editPath} leftSection={<IconPencil size={15} />}>
                {tc('actions.edit')}
              </Button>
            </Group>

            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <ActionIcon variant="subtle" color="gray" aria-label={t('card.more', { name: profile.name })}>
                  <IconDots size={18} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item leftSection={<IconCopyPlus size={16} />} onClick={onDuplicate}>
                  {tc('actions.duplicate')}
                </Menu.Item>
                <Menu.Item
                  color="red"
                  leftSection={<IconTrash size={16} />}
                  disabled={!canDelete}
                  onClick={onDelete}
                  title={canDelete ? undefined : t('card.onlyOne')}
                >
                  {t('card.delete')}
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </Stack>
      </Group>
    </Card>
  );
}
