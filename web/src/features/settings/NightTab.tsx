import { Alert, Badge, Group, Select, SimpleGrid, Slider, Stack, Switch, Text, ThemeIcon } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import {
  IconBrightnessDown,
  IconBulbOff,
  IconCircleOff,
  IconClock,
  IconInfoCircle,
  IconLock,
  IconMoonStars,
  IconPower,
  IconZzz,
  type Icon,
} from '@tabler/icons-react';
import { useLive } from '@/api/live';
import type { NightAction } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';
import { NumField, TimeField } from './fields';
import { Section } from './Section';
import { wrapsPastMidnight } from './time';

const ACTIONS: readonly NightAction[] = ['none', 'dim', 'lightsOff', 'sleep'];
const ACTION_ICONS: Record<NightAction, Icon> = {
  none: IconCircleOff,
  dim: IconBrightnessDown,
  lightsOff: IconBulbOff,
  sleep: IconZzz,
};

interface ActionSelectProps {
  label: string;
  value: NightAction;
  onChange: (action: NightAction) => void;
  disabled?: boolean;
}

function ActionSelect({ label, value, onChange, disabled }: ActionSelectProps) {
  const { t } = useTranslation('settings');
  const Glyph = ACTION_ICONS[value];
  return (
    <Select
      label={label}
      description={t(`night.actionHelp.${value}`)}
      allowDeselect={false}
      disabled={disabled}
      value={value}
      leftSection={<Glyph size={17} stroke={1.7} />}
      data={ACTIONS.map((action) => ({ value: action, label: t(`night.actions.${action}`) }))}
      onChange={(next) => next && onChange(next as NightAction)}
      renderOption={({ option }) => {
        const OptionGlyph = ACTION_ICONS[option.value as NightAction];
        return (
          <Group gap="sm" wrap="nowrap">
            <OptionGlyph size={18} stroke={1.7} style={{ flex: 'none' }} />
            <div>
              <Text size="sm">{option.label}</Text>
              <Text size="xs" c="dimmed">
                {t(`night.actionHelp.${option.value}`)}
              </Text>
            </div>
          </Group>
        );
      }}
    />
  );
}

/** Settings → Night & standby. */
export function NightTab() {
  const { t } = useTranslation('settings');
  const { t: tc } = useTranslation('common');
  const night = useConfigDraft((s) => s.draft?.night);
  const update = useConfigDraft((s) => s.update);
  const current = useLive((s) => s.state?.engine.night);
  if (!night) return null;

  const set = (patch: Partial<typeof night>) =>
    update((d) => {
      Object.assign(d.night, patch);
    });
  const wraps = wrapsPastMidnight(night.quietFrom, night.quietTo);

  return (
    <Stack gap="lg">
      <Group gap="xs">
        <Text size="sm" c="dimmed">
          {t('night.now')}
        </Text>
        <Badge variant="light" color={current && current !== 'none' ? 'violet' : 'gray'}>
          {tc(`night.${current ?? 'none'}`)}
        </Badge>
      </Group>

      <Section title={t('night.triggersTitle')} description={t('night.triggersDescription')} icon={<IconLock size={20} />}>
        <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
          <ActionSelect label={t('night.onLock')} value={night.onLock} onChange={(onLock) => set({ onLock })} />
          <Stack gap="sm">
            <ActionSelect label={t('night.onIdle')} value={night.onIdle} onChange={(onIdle) => set({ onIdle })} />
            <NumField
              label={t('night.idleMinutes')}
              description={t('night.idleMinutesHelp')}
              disabled={night.onIdle === 'none'}
              value={night.idleMinutes}
              min={1}
              max={1440}
              allowDecimal={false}
              suffix={` ${t('night.minutes')}`}
              maw={240}
              onCommit={(idleMinutes) => set({ idleMinutes })}
            />
          </Stack>
        </SimpleGrid>
        <Alert variant="light" color="gray" icon={<IconInfoCircle size={18} />} p="sm">
          <Text size="sm">{t('night.trayNote')}</Text>
        </Alert>
      </Section>

      <Section title={t('night.quietTitle')} description={t('night.quietDescription')} icon={<IconMoonStars size={20} />}>
        <Switch
          checked={night.quietHoursEnabled}
          onChange={(event) => set({ quietHoursEnabled: event.currentTarget.checked })}
          label={t('night.quietEnabled')}
        />
        <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
          <Stack gap="sm">
            <SimpleGrid cols={2} spacing="sm">
              <TimeField
                label={t('night.from')}
                leftSection={<IconClock size={16} />}
                disabled={!night.quietHoursEnabled}
                value={night.quietFrom}
                onChange={(quietFrom) => set({ quietFrom })}
              />
              <TimeField
                label={t('night.to')}
                leftSection={<IconClock size={16} />}
                disabled={!night.quietHoursEnabled}
                value={night.quietTo}
                onChange={(quietTo) => set({ quietTo })}
              />
            </SimpleGrid>
            {night.quietHoursEnabled && (
              <Text size="xs" c={night.quietFrom.slice(0, 5) === night.quietTo.slice(0, 5) ? 'yellow.7' : 'dimmed'}>
                {night.quietFrom.slice(0, 5) === night.quietTo.slice(0, 5) ? t('night.quietSame') : wraps ? t('night.quietWraps') : t('night.quietSameDay')}
              </Text>
            )}
          </Stack>
          <ActionSelect
            label={t('night.quietAction')}
            value={night.quietAction}
            disabled={!night.quietHoursEnabled}
            onChange={(quietAction) => set({ quietAction })}
          />
        </SimpleGrid>
      </Section>

      <Section title={t('night.dimTitle')} description={t('night.dimDescription')} icon={<IconBrightnessDown size={20} />}>
        <Group gap="md" wrap="nowrap">
          <Slider
            style={{ flex: 1 }}
            min={0}
            max={100}
            step={1}
            value={night.dimPercent}
            label={(value) => `${value}%`}
            thumbLabel={t('night.dimPercent')}
            marks={[{ value: 0 }, { value: 25 }, { value: 50 }, { value: 75 }, { value: 100 }]}
            onChange={(dimPercent) => set({ dimPercent })}
          />
          <Text ff="monospace" fw={600} w={48} ta="right">
            {night.dimPercent}%
          </Text>
        </Group>
        <Text size="xs" c="dimmed" mt={-4}>
          {t('night.dimPercentHelp')}
        </Text>
        <Switch
          checked={night.sleepOnShutdown}
          onChange={(event) => set({ sleepOnShutdown: event.currentTarget.checked })}
          label={t('night.sleepOnShutdown')}
          description={t('night.sleepOnShutdownHelp')}
        />
      </Section>

      <Section title={t('night.legendTitle')} icon={<IconPower size={20} />}>
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          {ACTIONS.map((action) => {
            const Glyph = ACTION_ICONS[action];
            return (
              <Group key={action} gap="sm" wrap="nowrap" align="flex-start">
                <ThemeIcon variant="light" color="gray" size={32} radius="md">
                  <Glyph size={18} stroke={1.7} />
                </ThemeIcon>
                <div>
                  <Text size="sm" fw={600}>
                    {t(`night.actions.${action}`)}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {t(`night.actionHelp.${action}`)}
                  </Text>
                </div>
              </Group>
            );
          })}
        </SimpleGrid>
      </Section>
    </Stack>
  );
}
