import { useEffect, useState } from 'react';
import { Button, Code, Collapse, Group, Loader, Paper, SimpleGrid, Stack, Text, ThemeIcon, Title } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { Trans, useTranslation } from 'react-i18next';
import { IconChevronDown, IconPlugConnectedX } from '@tabler/icons-react';
import type { HubStatus } from '@/api/types';
import classes from './HubNotice.module.css';

/** Explains how to get the dials talking to VU Studio when the hub is not connected. */
export function HubNotice({ hub }: { hub: HubStatus | undefined }) {
  const { t } = useTranslation('dashboard');
  const state = hub?.state ?? 'disconnected';
  const connecting = state === 'connecting';
  const error = state === 'error';

  // Steps are open on wide screens; on phones they fold away so the dials stay in view.
  const wide = useMediaQuery('(min-width: 48em)', true);
  const [open, setOpen] = useState(wide);
  useEffect(() => setOpen(wide), [wide]);

  const steps = [
    t('offline.step1'),
    t('offline.step2'),
    <Trans key="sim" t={t} i18nKey="offline.step3" values={{ flag: '--simulate 4' }} components={{ code: <Code className={classes.code} /> }} />,
  ];

  return (
    <Paper className={classes.notice} data-state={state} p={{ base: 'md', sm: 'lg' }} withBorder role="status">
      <Group gap="md" wrap="nowrap" align="flex-start">
        <ThemeIcon size={44} radius="xl" variant="light" color={error ? 'red' : 'amber'} className={classes.badge}>
          {connecting ? <Loader size={20} color="amber" type="dots" /> : <IconPlugConnectedX size={24} stroke={1.6} />}
        </ThemeIcon>
        <Stack gap={4} miw={0} style={{ flex: 1 }}>
          <Title order={2} size="h4">
            {connecting ? t('offline.titleConnecting') : error ? t('offline.titleError') : t('offline.title')}
          </Title>
          <Text size="sm" c="dimmed">
            {t('offline.intro')}
          </Text>
          {hub?.message && (
            <Text size="xs" c="dimmed" ff="monospace">
              {t('offline.details', { message: hub.message })}
            </Text>
          )}
          {!wide && (
            <Group>
              <Button
                variant="subtle"
                size="compact-sm"
                px={0}
                onClick={() => setOpen((o) => !o)}
                aria-expanded={open}
                rightSection={<IconChevronDown size={16} className={classes.chevron} data-open={open || undefined} />}
              >
                {open ? t('offline.hideSteps') : t('offline.showSteps')}
              </Button>
            </Group>
          )}
        </Stack>
      </Group>

      <Collapse in={open}>
        <SimpleGrid cols={{ base: 1, md: 3 }} spacing="sm" mt="md" component="ol" className={classes.steps}>
          {steps.map((text, i) => (
            <li key={i} className={classes.step}>
              <span className={classes.stepNumber} aria-hidden>
                {i + 1}
              </span>
              <Text size="sm" className={classes.stepText}>
                {text}
              </Text>
            </li>
          ))}
        </SimpleGrid>
      </Collapse>
    </Paper>
  );
}
