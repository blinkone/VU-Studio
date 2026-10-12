import { useState } from 'react';
import { ActionIcon, Button, Card, Group, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { IconAdjustmentsHorizontal, IconBulb, IconChevronLeft, IconChevronRight } from '@tabler/icons-react';
import { api } from '@/api/client';
import type { DialStateSnapshot } from '@/api/types';
import { DialPreview } from './DialPreview';
import classes from './DialCard.module.css';

interface DialCardProps {
  dial: DialStateSnapshot;
}

/** Dashboard card: the live dial plus page, identify and edit actions. */
export function DialCard({ dial }: DialCardProps) {
  const { t } = useTranslation('dashboard');
  const [busy, setBusy] = useState(false);
  const editPath = `/dials/${dial.slot}`;
  const multiPage = dial.pageCount > 1;
  const canIdentify = dial.online && !!dial.uid;

  const run = async (action: () => Promise<unknown>, success?: string) => {
    setBusy(true);
    try {
      await action();
      if (success) notifications.show({ message: success, color: 'teal' });
    } catch {
      notifications.show({ message: t('actions.failed'), color: 'red' });
    } finally {
      setBusy(false);
    }
  };

  const advance = (delta: 1 | -1) => void run(() => api.post(`/api/engine/slots/${dial.slot}/advance`, { delta }));
  const identify = () =>
    void run(() => api.post(`/api/hardware/dials/${encodeURIComponent(dial.uid!)}/identify`), t('actions.identifySent', { slot: dial.slot }));

  return (
    <Card className={classes.card} padding="lg" data-alarm={dial.alarmActive || undefined}>
      <DialPreview slot={dial.slot} state={dial} ambient to={editPath} />

      <div className={classes.actions} data-pager={multiPage || undefined}>
        {multiPage ? (
          <div className={classes.pager}>
            <ActionIcon variant="subtle" color="gray" size="lg" disabled={busy} onClick={() => advance(-1)} aria-label={t('actions.previous')}>
              <IconChevronLeft size={18} />
            </ActionIcon>
            <span className={classes.pageCount} aria-hidden>
              {dial.pageIndex + 1}/{dial.pageCount}
            </span>
            <ActionIcon variant="subtle" color="gray" size="lg" disabled={busy} onClick={() => advance(1)} aria-label={t('actions.next')}>
              <IconChevronRight size={18} />
            </ActionIcon>
          </div>
        ) : (
          <span />
        )}

        <Group gap={6} wrap="nowrap">
          <Tooltip label={canIdentify ? t('actions.identifyHint') : t('actions.identifyOffline')}>
            <span>
              <ActionIcon variant="default" size="lg" disabled={!canIdentify || busy} onClick={identify} aria-label={t('actions.identify')}>
                <IconBulb size={18} />
              </ActionIcon>
            </span>
          </Tooltip>
          <Button
            component={Link}
            to={editPath}
            variant="light"
            size="sm"
            leftSection={<IconAdjustmentsHorizontal size={16} />}
            aria-label={t('actions.editDial', { slot: dial.slot })}
            className={classes.editButton}
          >
            <span className={classes.editText}>{t('actions.edit')}</span>
          </Button>
        </Group>
      </div>
    </Card>
  );
}
