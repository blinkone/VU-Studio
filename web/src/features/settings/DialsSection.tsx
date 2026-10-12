import { useState } from 'react';
import {
  Badge,
  Button,
  Group,
  Paper,
  Select,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  ThemeIcon,
  Tooltip,
} from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconFocus2, IconGauge, IconRuler2, IconTrash } from '@tabler/icons-react';
import { api } from '@/api/client';
import { useConfigDraft } from '@/state/configDraft';
import { runAction } from './actions';
import { CopyAction } from './CopyAction';
import { ensureDial, setDialSlot, useDialRows, type DialRow } from './dials';
import { EasingSelect } from './EasingSelect';
import { Section } from './Section';

interface DialsSectionProps {
  onCalibrate: (uid?: string) => void;
}

/** The dials the hub reports, merged with what the configuration remembers about each of them. */
export function DialsSection({ onCalibrate }: DialsSectionProps) {
  const { t } = useTranslation('settings');
  const rows = useDialRows();
  const slotMax = Math.max(8, ...rows.map((r) => r.slot ?? 0));
  const hasOnline = rows.some((r) => r.online);

  return (
    <Section
      title={t('dials.title')}
      description={t('dials.description')}
      icon={<IconGauge size={20} />}
      right={
        <Button variant="light" size="xs" visibleFrom="sm" leftSection={<IconRuler2 size={15} />} disabled={!hasOnline} onClick={() => onCalibrate()}>
          {t('dials.calibrate')}
        </Button>
      }
    >
      {rows.length === 0 ? (
        <Paper withBorder p="lg" ta="center">
          <Text fw={500}>{t('dials.empty')}</Text>
          <Text size="sm" c="dimmed" mt={4}>
            {t('dials.emptyHelp')}
          </Text>
        </Paper>
      ) : (
        <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="md">
          {rows.map((row) => (
            <DialCard key={row.uid} row={row} rows={rows} slotMax={slotMax} onCalibrate={() => onCalibrate(row.uid)} />
          ))}
        </SimpleGrid>
      )}
    </Section>
  );
}

interface DialCardProps {
  row: DialRow;
  rows: readonly DialRow[];
  slotMax: number;
  onCalibrate: () => void;
}

function DialCard({ row, rows, slotMax, onCalibrate }: DialCardProps) {
  const { t } = useTranslation('settings');
  const update = useConfigDraft((s) => s.update);
  const [identifying, setIdentifying] = useState(false);

  const slotLabel = row.slot !== undefined ? t('dials.slotN', { n: row.slot }) : t('dials.slotNone');
  const status = row.info ? (row.online ? 'online' : 'offline') : 'absent';
  const takenBy = new Map(rows.filter((r) => r.slot !== undefined && r.uid !== row.uid).map((r) => [r.slot as number, r]));

  const setName = (value: string) =>
    update((d) => {
      const dial = ensureDial(d.hardware.dials, row.uid);
      if (value.trim()) dial.name = value;
      else delete dial.name;
    });

  const identify = async () => {
    setIdentifying(true);
    await runAction(() => api.post(`/api/hardware/dials/${encodeURIComponent(row.uid)}/identify`), { error: t('dials.identifyFailed') });
    setIdentifying(false);
  };

  return (
    <Paper withBorder p="md" radius="lg">
      <Stack gap="md">
        <Group wrap="nowrap" align="flex-start" gap="sm">
          <ThemeIcon size={40} radius="md" variant="light" color={row.online ? undefined : 'gray'} style={{ flex: 'none', marginTop: 22 }}>
            <Text fw={700}>{row.slot ?? '–'}</Text>
          </ThemeIcon>
          <TextInput
            style={{ flex: 1, minWidth: 0 }}
            label={t('dials.name')}
            placeholder={slotLabel}
            maxLength={32}
            value={row.name ?? ''}
            onChange={(event) => setName(event.currentTarget.value)}
            onBlur={(event) => {
              const trimmed = event.currentTarget.value.trim();
              if (trimmed !== (row.name ?? '')) setName(trimmed);
            }}
          />
          <Badge
            mt={26}
            variant="light"
            color={status === 'online' ? 'teal' : status === 'offline' ? 'red' : 'gray'}
            style={{ flex: 'none' }}
          >
            {t(`dials.status.${status}`)}
          </Badge>
        </Group>

        <Group gap={6} wrap="wrap">
          <Text size="xs" c="dimmed" ff="monospace" title={row.uid}>
            {t('dials.uid')} {row.uid.length > 14 ? `${row.uid.slice(0, 6)}…${row.uid.slice(-4)}` : row.uid}
          </Text>
          <CopyAction size="xs" value={row.uid} label={t('dials.copyUid')} />
          {row.info && (
            <Text size="xs" c="dimmed">
              · {t('dials.bus', { n: row.info.busIndex })}
              {row.info.firmwareVersion ? ` · ${t('dials.firmware')} ${row.info.firmwareVersion}` : ''}
              {row.info.hardwareVersion ? ` · ${t('dials.hardware')} ${row.info.hardwareVersion}` : ''}
            </Text>
          )}
        </Group>

        <Select
          label={t('dials.slot')}
          description={t('dials.slotHelp')}
          allowDeselect={false}
          placeholder={t('dials.slotNone')}
          value={row.slot !== undefined ? String(row.slot) : null}
          data={Array.from({ length: slotMax }, (_, i) => ({ value: String(i + 1), label: t('dials.slotN', { n: i + 1 }) }))}
          onChange={(value) => {
            if (value) update((d) => setDialSlot(d.hardware.dials, row.uid, Number(value)));
          }}
          renderOption={({ option }) => {
            const other = takenBy.get(Number(option.value));
            return (
              <Group justify="space-between" w="100%" wrap="nowrap" gap="xs">
                <Text size="sm">{option.label}</Text>
                {other && (
                  <Text size="xs" c="dimmed" truncate>
                    {t('dials.swapWith', { name: other.name ?? t('dials.slotN', { n: other.slot }) })}
                  </Text>
                )}
              </Group>
            );
          }}
        />

        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          <EasingSelect
            kind="needle"
            label={t('dials.needleMotion')}
            value={row.settings?.needleEasing}
            onChange={(easing) =>
              update((d) => {
                const dial = ensureDial(d.hardware.dials, row.uid);
                if (easing) dial.needleEasing = easing;
                else delete dial.needleEasing;
              })
            }
          />
          <EasingSelect
            kind="backlight"
            label={t('dials.lightMotion')}
            value={row.settings?.backlightEasing}
            onChange={(easing) =>
              update((d) => {
                const dial = ensureDial(d.hardware.dials, row.uid);
                if (easing) dial.backlightEasing = easing;
                else delete dial.backlightEasing;
              })
            }
          />
        </SimpleGrid>

        <Group gap="xs" justify="space-between">
          <Group gap="xs">
            <Button size="xs" variant="default" leftSection={<IconFocus2 size={15} />} loading={identifying} disabled={!row.online} onClick={() => void identify()}>
              {t('dials.identify')}
            </Button>
            <Button size="xs" variant="default" leftSection={<IconRuler2 size={15} />} disabled={!row.online} onClick={onCalibrate}>
              {t('dials.calibrate')}
            </Button>
          </Group>
          {!row.info && (
            <Tooltip label={t('dials.forgetHelp')} multiline w={240}>
              <Button
                size="xs"
                variant="subtle"
                color="red"
                leftSection={<IconTrash size={15} />}
                onClick={() =>
                  update((d) => {
                    d.hardware.dials = d.hardware.dials.filter((x) => x.uid.toLowerCase() !== row.uid.toLowerCase());
                  })
                }
              >
                {t('dials.forget')}
              </Button>
            </Tooltip>
          )}
        </Group>
      </Stack>
    </Paper>
  );
}
