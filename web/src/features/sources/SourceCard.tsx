import { ActionIcon, Anchor, Badge, Card, Group, Switch, Text, Tooltip } from '@mantine/core';
import { IconDeviceDesktop, IconEye, IconLock, IconPencil, IconRefresh, IconTrash } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import type { ProviderInstanceConfig, ProviderStatusInfo, ProviderTypeDto } from '@/api/types';
import { localized } from '@/i18n';
import { HealthBadge, type HealthKind } from './HealthBadge';
import { ProviderIcon } from './ProviderIcon';
import classes from './sources.module.css';

export type PendingState = 'new' | 'modified' | null;

interface SourceCardProps {
  instance: ProviderInstanceConfig;
  /** Undefined when the configuration names a type this build does not know. */
  type: ProviderTypeDto | undefined;
  status: ProviderStatusInfo | undefined;
  pending: PendingState;
  sensorCount: number;
  /** A local-only source seen from another device: view, but no changes. */
  locked: boolean;
  onToggle: (enabled: boolean) => void;
  onEdit: () => void;
  onDelete: () => void;
}

function healthKind(instance: ProviderInstanceConfig, status: ProviderStatusInfo | undefined, pending: PendingState): HealthKind {
  if (pending === 'new') return 'notStarted';
  if (!instance.enabled) return 'disabled';
  if (!status) return 'starting';
  // Switched on in the draft but the service still has it stopped: the change is not saved yet.
  if (status.health === 'stopped') return 'notStarted';
  return status.health;
}

export function SourceCard({ instance, type, status, pending, sensorCount, locked, onToggle, onEdit, onDelete }: SourceCardProps) {
  const { t, i18n } = useTranslation('sources');
  const descriptor = type?.descriptor;
  const typeName = descriptor ? localized(descriptor.name, i18n.language) : instance.type;
  const name = instance.name?.trim() || typeName;
  const kind = healthKind(instance, status, pending);
  const showMessage = instance.enabled && pending !== 'new' && !!status?.message;
  const restarts = instance.enabled && pending !== 'new' ? (status?.restarts ?? 0) : 0;
  const unsupported = type !== undefined && !type.supported;

  return (
    <Card component="article" padding="md" className={classes.source} data-off={!instance.enabled || undefined} aria-label={name}>
      <Group wrap="nowrap" align="flex-start" gap="sm">
        <div className={`${classes.tile} ${instance.enabled && kind === 'ok' ? classes.tileOn : ''}`}>
          <ProviderIcon icon={descriptor?.icon} size={24} />
        </div>
        <div style={{ minWidth: 0, flex: 1 }}>
          <Group justify="space-between" wrap="nowrap" gap="xs">
            <Text fw={600} truncate className={classes.title} title={name}>
              {name}
            </Text>
            <Tooltip label={t('card.lockedHint')} disabled={!locked} multiline w={260}>
              {/* A span, because disabled inputs do not emit the pointer events a tooltip needs. */}
              <span style={{ display: 'inline-flex' }}>
                <Switch
                  size="sm"
                  checked={instance.enabled}
                  disabled={locked}
                  onChange={(event) => onToggle(event.currentTarget.checked)}
                  aria-label={t('card.enabledAria', { name })}
                />
              </span>
            </Tooltip>
          </Group>
          <Text size="xs" c="dimmed" truncate>
            {typeName} · <span className={classes.id}>{instance.id}</span>
          </Text>
        </div>
      </Group>

      <Group gap={6}>
        <Tooltip label={status?.since && kind === 'ok' ? t('card.since', { time: new Date(status.since).toLocaleString(i18n.language) }) : undefined} disabled={!(status?.since && kind === 'ok')}>
          <span>
            <HealthBadge kind={kind} />
          </span>
        </Tooltip>
        {pending && (
          <Badge variant="outline" color="amber" size="md">
            {t(pending === 'new' ? 'card.pendingNew' : 'card.pendingModified')}
          </Badge>
        )}
        {unsupported && (
          <Badge variant="light" color="gray" size="md">
            {t('card.unsupported')}
          </Badge>
        )}
        {type === undefined && (
          <Badge variant="light" color="red" size="md">
            {t('card.unknownType')}
          </Badge>
        )}
        {descriptor?.runsInUserSession && (
          <Tooltip label={t('badges.desktopHint')} multiline w={260}>
            <Badge variant="default" size="md" leftSection={<IconDeviceDesktop size={13} aria-hidden />}>
              {t('badges.desktop')}
            </Badge>
          </Tooltip>
        )}
        {descriptor?.localConfigOnly && (
          <Tooltip label={locked ? t('card.lockedHint') : t('badges.localOnlyHint')} multiline w={260}>
            <Badge variant="default" size="md" leftSection={<IconLock size={13} aria-hidden />}>
              {t('badges.localOnly')}
            </Badge>
          </Tooltip>
        )}
      </Group>

      {(showMessage || restarts > 0) && (
        <div>
          {showMessage && (
            <Text size="xs" c="dimmed" lineClamp={3}>
              {status?.message}
            </Text>
          )}
          {restarts > 0 && (
            <Text size="xs" c="dimmed" mt={showMessage ? 2 : 0} style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
              <IconRefresh size={12} aria-hidden /> {t('card.restarts', { count: restarts })}
            </Text>
          )}
        </div>
      )}

      <Group justify="space-between" mt="auto" pt={4} wrap="nowrap">
        {sensorCount > 0 ? (
          <Anchor component={Link} to={`/sensors?provider=${encodeURIComponent(instance.id)}`} size="sm">
            {t('card.sensors', { count: sensorCount })}
          </Anchor>
        ) : (
          <Text size="sm" c="dimmed">
            {t('card.noSensors')}
          </Text>
        )}
        <Group gap={4} wrap="nowrap">
          <Tooltip label={locked || !descriptor ? t('card.view') : t('card.edit')}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={onEdit}
              disabled={!descriptor}
              aria-label={t(locked ? 'card.viewAria' : 'card.editAria', { name })}
            >
              {locked ? <IconEye size={18} /> : <IconPencil size={18} />}
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('card.delete')}>
            <ActionIcon variant="subtle" color="red" onClick={onDelete} disabled={locked} aria-label={t('card.deleteAria', { name })}>
              <IconTrash size={18} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </Group>
    </Card>
  );
}
