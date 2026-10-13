import { Badge, Button, Card, Group, Text, Tooltip } from '@mantine/core';
import { IconCheck, IconDeviceDesktop, IconLock, IconPlus } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { ProviderTypeDto } from '@/api/types';
import { localized } from '@/i18n';
import { parsePlatforms } from './groups';
import { ProviderIcon } from './ProviderIcon';
import classes from './sources.module.css';

interface TypeCardProps {
  type: ProviderTypeDto;
  /** How many instances of this type are configured. */
  configured: number;
  /** A local-only type seen from another device: it cannot be added from here. */
  locked: boolean;
  onAdd: () => void;
  /** For singleton types that already exist: jump to their editor. */
  onEditExisting: () => void;
}

export function TypeCard({ type, configured, locked, onAdd, onEditExisting }: TypeCardProps) {
  const { t, i18n } = useTranslation('sources');
  const { descriptor, supported } = type;
  const name = localized(descriptor.name, i18n.language);
  const description = localized(descriptor.description, i18n.language);
  const platforms = parsePlatforms(descriptor.platforms);
  const added = descriptor.singleton && configured > 0;
  const available = supported && !locked;

  return (
    <Card component="article" padding="md" className={classes.type} data-available={available} aria-label={name}>
      <Group wrap="nowrap" align="flex-start" gap="sm">
        <div className={classes.tile}>
          <ProviderIcon icon={descriptor.icon} size={24} />
        </div>
        <div className={classes.typeText} style={{ minWidth: 0, flex: 1 }}>
          <Text fw={600} lh={1.3}>
            {name}
          </Text>
          {description && (
            <Text size="sm" c="dimmed" lineClamp={3} mt={2}>
              {description}
            </Text>
          )}
        </div>
      </Group>

      <Group gap={6} className={classes.typeText}>
        {platforms.map((platform) => (
          <Badge key={platform} variant="outline" color="gray" size="sm" radius="sm">
            {t(`platforms.${platform}`)}
          </Badge>
        ))}
        {descriptor.runsInUserSession && (
          <Tooltip label={t('badges.desktopHint')} multiline w={260}>
            <Badge variant="default" size="sm" radius="sm" leftSection={<IconDeviceDesktop size={12} aria-hidden />}>
              {t('badges.desktop')}
            </Badge>
          </Tooltip>
        )}
        {descriptor.localConfigOnly && (
          <Tooltip label={t('badges.localOnlyHint')} multiline w={260}>
            <Badge variant="default" size="sm" radius="sm" leftSection={<IconLock size={12} aria-hidden />}>
              {t('badges.localOnly')}
            </Badge>
          </Tooltip>
        )}
      </Group>

      <Group justify="space-between" mt="auto" pt={4} wrap="nowrap" gap="xs">
        {!supported ? (
          <Text size="sm" c="dimmed">
            {t('gallery.unavailable')}
          </Text>
        ) : added ? (
          <Badge variant="light" color="teal" size="md" leftSection={<IconCheck size={13} aria-hidden />}>
            {t('gallery.added')}
          </Badge>
        ) : configured > 0 ? (
          <Text size="sm" c="dimmed">
            {t('gallery.configured', { count: configured })}
          </Text>
        ) : (
          <span />
        )}

        {added ? (
          <Button variant="default" size="xs" onClick={onEditExisting} aria-label={t('gallery.editAria', { name })}>
            {t('card.edit')}
          </Button>
        ) : (
          <Tooltip label={t('card.lockedAddHint')} disabled={!(supported && locked)} multiline w={260}>
            <span>
              <Button
                variant="light"
                size="xs"
                leftSection={<IconPlus size={14} />}
                disabled={!available}
                onClick={onAdd}
                aria-label={t('gallery.addAria', { name })}
              >
                {t('gallery.add')}
              </Button>
            </span>
          </Tooltip>
        )}
      </Group>
    </Card>
  );
}
