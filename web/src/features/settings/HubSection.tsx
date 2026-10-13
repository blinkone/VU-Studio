import { useState } from 'react';
import { Alert, Badge, Button, Group, SimpleGrid, Stack, Switch, Text, TextInput } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconInfoCircle, IconRefresh, IconSearch, IconUsb } from '@tabler/icons-react';
import { api } from '@/api/client';
import { useLive } from '@/api/live';
import { useInfo } from '@/api/queries';
import { useConfigDraft } from '@/state/configDraft';
import { runAction } from './actions';
import { NumField } from './fields';
import { Section } from './Section';

const MAX_SIMULATED = 16;

/** Connection to the VU1 hub: live status, serial port, simulator and bus rescan. */
export function HubSection() {
  const { t } = useTranslation('settings');
  const connection = useLive((s) => s.status);
  // The hub object is re-created by every state message: pick primitives instead.
  const hubState = useLive((s) => s.state?.hub.state);
  const hubPort = useLive((s) => s.state?.hub.port);
  const hubMessage = useLive((s) => s.state?.hub.message);
  const hubSimulated = useLive((s) => s.state?.hub.simulated ?? false);
  const { data: info } = useInfo();
  const port = useConfigDraft((s) => s.draft?.hardware.port ?? 'auto');
  const simulate = useConfigDraft((s) => s.draft?.hardware.simulate ?? 0);
  const update = useConfigDraft((s) => s.update);
  const [busy, setBusy] = useState<'rescan' | 'provision' | null>(null);

  const auto = port.trim().toLowerCase() === 'auto' || port.trim() === '';
  const [portText, setPortText] = useState(auto ? '' : port);

  const connected = connection === 'open' && hubState === 'connected';
  const status =
    connection !== 'open'
      ? { color: 'gray', label: t('hub.status.service') }
      : hubState === 'connected'
        ? hubSimulated
          ? { color: 'grape', label: t('hub.status.simulated') }
          : { color: 'teal', label: t('hub.status.connected', { port: hubPort ?? '?' }) }
        : hubState === 'connecting'
          ? { color: 'yellow', label: t('hub.status.connecting') }
          : hubState === 'error'
            ? { color: 'red', label: t('hub.status.error') }
            : { color: 'gray', label: t('hub.status.none') };

  const defaultPort = info?.os.toLowerCase().includes('windows') ? 'COM3' : '/dev/ttyUSB0';

  const scan = async (provision: boolean) => {
    setBusy(provision ? 'provision' : 'rescan');
    await runAction(() => api.post('/api/hardware/rescan', { provision }), {
      success: provision ? t('hub.provisioned') : t('hub.rescanned'),
      error: t('hub.rescanFailed'),
    });
    setBusy(null);
  };

  return (
    <Section
      title={t('hub.title')}
      description={t('hub.description')}
      icon={<IconUsb size={20} />}
      right={
        <Badge variant="light" color={status.color} size="lg" style={{ flex: 'none' }}>
          {status.label}
        </Badge>
      }
    >
      <Stack gap="lg">
        {hubMessage && connection === 'open' && hubState !== 'connected' && (
          <Alert variant="light" color={hubState === 'error' ? 'red' : 'gray'} icon={<IconInfoCircle size={18} />} p="sm">
            <Text size="sm">{hubMessage}</Text>
          </Alert>
        )}

        <Stack gap="xs">
          <Switch
            checked={auto}
            label={t('hub.auto')}
            description={t('hub.autoHelp')}
            onChange={(event) => {
              if (event.currentTarget.checked) {
                update((d) => {
                  d.hardware.port = 'auto';
                });
              } else {
                const suggested = hubPort && !hubSimulated ? hubPort : defaultPort;
                setPortText(suggested);
                update((d) => {
                  d.hardware.port = suggested;
                });
              }
            }}
          />
          {!auto && (
            <TextInput
              label={t('hub.port')}
              description={t('hub.portHelp')}
              placeholder={defaultPort}
              required
              maw={320}
              value={portText}
              error={portText.trim() === '' ? t('validation.required') : undefined}
              onChange={(event) => {
                const value = event.currentTarget.value;
                setPortText(value);
                if (value.trim() && value.trim().toLowerCase() !== 'auto') {
                  update((d) => {
                    d.hardware.port = value.trim();
                  });
                }
              }}
              onBlur={() => {
                if (portText.trim() === '') setPortText(port);
              }}
            />
          )}
        </Stack>

        <NumField
          label={t('hub.simulate')}
          description={t('hub.simulateHelp')}
          maw={320}
          min={0}
          max={MAX_SIMULATED}
          allowDecimal={false}
          value={simulate}
          onCommit={(count) =>
            update((d) => {
              d.hardware.simulate = count;
            })
          }
        />
        {info && info.simulate > 0 && (
          <Alert variant="light" color="grape" icon={<IconInfoCircle size={18} />} p="sm">
            <Text size="sm">{t('hub.simulateForced', { count: info.simulate })}</Text>
          </Alert>
        )}

        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          <Stack gap={6} align="flex-start">
            <Button
              variant="default"
              leftSection={<IconRefresh size={16} />}
              loading={busy === 'rescan'}
              disabled={!connected || busy === 'provision'}
              onClick={() => void scan(false)}
            >
              {t('hub.rescan')}
            </Button>
            <Text size="xs" c="dimmed">
              {t('hub.rescanHelp')}
            </Text>
          </Stack>
          <Stack gap={6} align="flex-start">
            <Button
              variant="light"
              leftSection={<IconSearch size={16} />}
              loading={busy === 'provision'}
              disabled={!connected || busy === 'rescan'}
              onClick={() => void scan(true)}
            >
              {t('hub.provision')}
            </Button>
            <Text size="xs" c="dimmed">
              {t('hub.provisionHelp')}
            </Text>
          </Stack>
        </SimpleGrid>
        {!connected && (
          <Group gap={6}>
            <IconInfoCircle size={14} aria-hidden />
            <Text size="xs" c="dimmed">
              {t('hub.scanNeedsHub')}
            </Text>
          </Group>
        )}
      </Stack>
    </Section>
  );
}
