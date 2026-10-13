import { Badge, Loader, type MantineColor } from '@mantine/core';
import { IconAlertOctagon, IconAlertTriangle, IconCircleCheck, IconCircleDashed, IconPlayerPause, IconPlugOff } from '@tabler/icons-react';
import type { Icon } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { ProviderHealth } from '@/api/types';

/** What the badge shows: the service's health, or one of two client-side states. */
export type HealthKind = ProviderHealth | 'disabled' | 'notStarted';

interface HealthMeta {
  color: MantineColor;
  icon: Icon | null;
}

// Color is never the only signal: every state has its own icon and label.
const META: Record<HealthKind, HealthMeta> = {
  ok: { color: 'teal', icon: IconCircleCheck },
  starting: { color: 'blue', icon: null },
  degraded: { color: 'yellow', icon: IconAlertTriangle },
  unavailable: { color: 'orange', icon: IconPlugOff },
  error: { color: 'red', icon: IconAlertOctagon },
  stopped: { color: 'gray', icon: IconPlayerPause },
  disabled: { color: 'gray', icon: IconPlayerPause },
  notStarted: { color: 'gray', icon: IconCircleDashed },
};

interface HealthBadgeProps {
  kind: HealthKind;
  size?: 'sm' | 'md';
}

export function HealthBadge({ kind, size = 'md' }: HealthBadgeProps) {
  const { t } = useTranslation('sources');
  const meta = META[kind];
  const Icon = meta.icon;
  return (
    <Badge
      variant="light"
      color={meta.color}
      size={size}
      leftSection={Icon ? <Icon size={13} stroke={2} aria-hidden /> : <Loader size={10} color={meta.color} aria-hidden />}
    >
      {t(`health.${kind}`)}
    </Badge>
  );
}
