import type { ReactNode } from 'react';
import { Badge, Card, Group, Paper, SimpleGrid, Skeleton, Stack, Text, ThemeIcon, Title, Tooltip } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { IconCloudOff, IconGauge, IconMoonStars, IconPlayerPause, IconStack2, IconUsb } from '@tabler/icons-react';
import { useLive } from '@/api/live';
import type { StateDto } from '@/api/types';
import { DialCard } from '@/features/dial/DialCard';
import { HubNotice } from '@/features/dial/HubNotice';

/** The first screen: every dial as it looks right now, with quick actions. */
export function DashboardPage() {
  const state = useLive((s) => s.state);
  const status = useLive((s) => s.status);

  const dials = state?.engine.dials ?? [];
  const hubConnected = state?.hub.state === 'connected';

  return (
    <Stack gap="xl" pb={96} maw={1600} mx="auto">
      <Header state={state} />

      {!state && status === 'closed' ? (
        <ConnectionLost />
      ) : !state ? (
        <DialGrid>
          {Array.from({ length: 4 }, (_, i) => (
            <Card key={i} padding="lg">
              <Skeleton height={14} width="40%" mb="md" />
              <Skeleton radius="lg" style={{ aspectRatio: '1.2' }} />
              <Skeleton height={18} width="70%" mt="md" />
            </Card>
          ))}
        </DialGrid>
      ) : (
        <>
          {!hubConnected && <HubNotice hub={state.hub} />}
          {dials.length === 0 ? (
            <Empty />
          ) : (
            <DialGrid>
              {dials.map((dial) => (
                <DialCard key={dial.slot} dial={dial} />
              ))}
            </DialGrid>
          )}
        </>
      )}
    </Stack>
  );
}

function DialGrid({ children }: { children: ReactNode }) {
  return (
    <SimpleGrid type="container" cols={{ base: 1, '460px': 2, '1000px': 4 }} spacing="lg" verticalSpacing="lg">
      {children}
    </SimpleGrid>
  );
}

function Header({ state }: { state: StateDto | null }) {
  const { t } = useTranslation('dashboard');
  const engine = state?.engine;
  const hub = state?.hub;
  const total = state?.engine.dials.filter((d) => d.uid).length ?? 0;
  const online = state?.engine.dials.filter((d) => d.online).length ?? 0;

  const hubColor = !hub ? 'gray' : hub.state === 'connected' ? (hub.simulated ? 'grape' : 'teal') : hub.state === 'error' ? 'red' : hub.state === 'connecting' ? 'amber' : 'gray';
  const hubLabel = !hub
    ? t('hub.connecting')
    : hub.state === 'connected'
      ? hub.simulated
        ? t('hub.simulated')
        : hub.port
          ? t('hub.connected', { port: hub.port })
          : t('hub.connectedNoPort')
      : t(`hub.${hub.state}`);

  return (
    <Group justify="space-between" align="flex-end" gap="md">
      <Stack gap={6} miw={0} style={{ flex: '1 1 320px' }}>
        <Group gap="sm" wrap="nowrap">
          <ThemeIcon size={36} radius="md" variant="light" color="amber" aria-hidden>
            <IconGauge size={22} stroke={1.7} />
          </ThemeIcon>
          <Title order={1} size="h2">
            {t('title')}
          </Title>
        </Group>
        <Text c="dimmed" size="sm" maw={560}>
          {t('subtitle')}
        </Text>
      </Stack>

      <Group gap="xs" wrap="wrap" style={{ flex: '0 1 auto' }}>
        {engine && (
          <Chip
            to="/profiles"
            color="amber"
            icon={<IconStack2 size={14} />}
            tooltip={t('profile.label')}
            label={
              engine.activeProfileName ? (
                <>
                  {engine.activeProfileName}
                  <Text span inherit c="dimmed" fw={500}>
                    {' · '}
                    {engine.manualProfileId ? t('profile.manual') : t('profile.automatic')}
                  </Text>
                </>
              ) : (
                t('profile.none')
              )
            }
          />
        )}
        {engine && engine.night !== 'none' && <Chip color="indigo" icon={<IconMoonStars size={14} />} label={t(`night.${engine.night}`)} />}
        {engine?.paused && <Chip color="yellow" icon={<IconPlayerPause size={14} />} label={t('paused')} />}
        <Chip
          color={hubColor}
          icon={<IconUsb size={14} />}
          tooltip={hub?.message}
          label={
            <>
              {hubLabel}
              {hub?.state === 'connected' && total > 0 && (
                <Text span inherit c="dimmed" fw={500}>
                  {' · '}
                  {t('hub.dialsOnline', { online, count: total })}
                </Text>
              )}
            </>
          }
        />
      </Group>
    </Group>
  );
}

interface ChipProps {
  label: ReactNode;
  icon: ReactNode;
  color: string;
  tooltip?: string;
  to?: string;
}

function Chip({ label, icon, color, tooltip, to }: ChipProps) {
  const props = {
    size: 'lg',
    radius: 'xl',
    variant: 'light',
    color,
    leftSection: icon,
    tt: 'none',
    fw: 600,
    styles: { root: { maxWidth: '100%', cursor: to ? 'pointer' : undefined }, label: { overflow: 'hidden', textOverflow: 'ellipsis' } },
  } as const;
  const badge = to ? (
    <Badge {...props} component={Link} to={to}>
      {label}
    </Badge>
  ) : (
    <Badge {...props}>{label}</Badge>
  );
  return tooltip ? <Tooltip label={tooltip}>{badge}</Tooltip> : badge;
}

function Empty() {
  const { t } = useTranslation('dashboard');
  return (
    <Paper withBorder p="xl" ta="center">
      <Stack align="center" gap="xs">
        <ThemeIcon size={56} radius="xl" variant="light" color="gray">
          <IconGauge size={30} stroke={1.5} />
        </ThemeIcon>
        <Title order={2} size="h4">
          {t('empty.title')}
        </Title>
        <Text c="dimmed" size="sm" maw={420}>
          {t('empty.text')}
        </Text>
      </Stack>
    </Paper>
  );
}

function ConnectionLost() {
  const { t } = useTranslation('dashboard');
  return (
    <Paper withBorder p="xl" ta="center" role="alert">
      <Stack align="center" gap="xs">
        <ThemeIcon size={56} radius="xl" variant="light" color="red">
          <IconCloudOff size={30} stroke={1.5} />
        </ThemeIcon>
        <Title order={2} size="h4">
          {t('lost.title')}
        </Title>
        <Text c="dimmed" size="sm" maw={420}>
          {t('lost.text')}
        </Text>
      </Stack>
    </Paper>
  );
}
