import { useEffect, useRef, useState } from 'react';
import {
  ActionIcon,
  Button,
  Chip,
  Group,
  Input,
  Menu,
  Paper,
  SegmentedControl,
  SimpleGrid,
  Stack,
  TagsInput,
  Text,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useTranslation } from 'react-i18next';
import { IconFocus2, IconPlus, IconTrash } from '@tabler/icons-react';
import { useLiveValue } from '@/api/hooks';
import type { ConditionMatch, Profile, ProfileCondition } from '@/api/types';
import { SensorPicker } from '@/components/SensorPicker';
import { formatValue } from '@/lib/format';
import { NumField, TimeField } from '@/features/settings/fields';
import { wrapsPastMidnight } from '@/features/settings/time';
import { CONDITION_ICONS } from './ConditionSummary';
import { useSensorLookup } from './hooks';
import {
  CONDITION_TYPES,
  DAYS,
  FOREGROUND_SENSOR,
  WEEKDAYS,
  WEEKEND,
  isScheduleEmpty,
  newCondition,
  weekdayName,
  type ConditionType,
} from './profileModel';
import classes from './profiles.module.css';

type ProcessCondition = Extract<ProfileCondition, { type: 'processRunning' | 'foregroundApp' }>;
type ScheduleCondition = Extract<ProfileCondition, { type: 'schedule' }>;
type SensorCondition = Extract<ProfileCondition, { type: 'sensor' }>;

interface ConditionsEditorProps {
  profile: Profile;
  /** Applies a change to this profile in the configuration draft. */
  edit: (recipe: (profile: Profile) => void) => void;
}

/** Match mode, list of conditions and the "add condition" menu. Only shown for automatic profiles. */
export function ConditionsEditor({ profile, edit }: ConditionsEditorProps) {
  const { t } = useTranslation('profiles');
  const { t: tc } = useTranslation('common');

  const setCondition = (index: number, next: ProfileCondition) =>
    edit((p) => {
      p.conditions[index] = next;
    });
  const remove = (index: number) =>
    edit((p) => {
      p.conditions.splice(index, 1);
    });
  const add = (type: ConditionType) =>
    edit((p) => {
      p.conditions.push(newCondition(type));
    });

  return (
    <Stack gap="md">
      {profile.conditions.length > 1 && (
        <Input.Wrapper label={t('conditions.matchLabel')} description={t(`conditions.matchHelp.${profile.match}`)}>
          <SegmentedControl
            mt={6}
            value={profile.match}
            onChange={(value) =>
              edit((p) => {
                p.match = value as ConditionMatch;
              })
            }
            data={[
              { value: 'any', label: t('conditions.match.any') },
              { value: 'all', label: t('conditions.match.all') },
            ]}
          />
        </Input.Wrapper>
      )}

      {profile.conditions.length === 0 && (
        <Text size="sm" c="dimmed">
          {t('conditions.empty')}
        </Text>
      )}

      {profile.conditions.map((condition, index) => {
        const Glyph = CONDITION_ICONS[condition.type];
        return (
          <Paper key={index} className={classes.condition} p="md">
            <Stack gap="sm">
              <Group justify="space-between" wrap="nowrap">
                <Group gap="xs" wrap="nowrap">
                  <Glyph size={18} stroke={1.7} aria-hidden />
                  <Text fw={600} size="sm">
                    {t(`conditions.types.${condition.type}.label`)}
                  </Text>
                </Group>
                <Tooltip label={tc('actions.remove')}>
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    onClick={() => remove(index)}
                    aria-label={`${tc('actions.remove')}: ${t(`conditions.types.${condition.type}.label`)}`}
                  >
                    <IconTrash size={17} />
                  </ActionIcon>
                </Tooltip>
              </Group>

              {(condition.type === 'processRunning' || condition.type === 'foregroundApp') && (
                <ProcessesEditor condition={condition} onChange={(next) => setCondition(index, next)} />
              )}
              {condition.type === 'schedule' && <ScheduleEditor condition={condition} onChange={(next) => setCondition(index, next)} />}
              {condition.type === 'sensor' && <SensorEditor condition={condition} onChange={(next) => setCondition(index, next)} />}
            </Stack>
          </Paper>
        );
      })}

      <Group>
        <Menu position="bottom-start" withinPortal width={320}>
          <Menu.Target>
            <Button variant="light" leftSection={<IconPlus size={16} />}>
              {t('conditions.add')}
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            {CONDITION_TYPES.map((type) => {
              const Glyph = CONDITION_ICONS[type];
              return (
                <Menu.Item key={type} leftSection={<Glyph size={18} stroke={1.7} />} onClick={() => add(type)}>
                  <Text size="sm" fw={500}>
                    {t(`conditions.types.${type}.label`)}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {t(`conditions.types.${type}.help`)}
                  </Text>
                </Menu.Item>
              );
            })}
          </Menu.Dropdown>
        </Menu>
      </Group>
    </Stack>
  );
}

// ---------------------------------------------------------------------------- programs

function ProcessesEditor({ condition, onChange }: { condition: ProcessCondition; onChange: (next: ProcessCondition) => void }) {
  const { t } = useTranslation('profiles');
  const live = useLiveValue(FOREGROUND_SENSOR);
  const { byId } = useSensorLookup();
  const foreground = typeof live?.t === 'string' && live.t.trim() ? live.t.trim() : undefined;
  const available = byId.has(FOREGROUND_SENSOR);
  const has = (name: string) => condition.processes.some((p) => p.trim().toLowerCase() === name.toLowerCase());

  const add = (name: string) => {
    if (!has(name)) onChange({ ...condition, processes: [...condition.processes, name] });
  };

  // "Detect": the user switches to the program during a short countdown and we take whatever is in
  // the foreground at the end — while this page is visible the foreground app is the browser itself.
  const [countdown, setCountdown] = useState<number | null>(null);
  const latest = useRef({ foreground, add });
  latest.current = { foreground, add };
  useEffect(() => {
    if (countdown === null) return;
    if (countdown <= 0) {
      setCountdown(null);
      const { foreground: name, add: addName } = latest.current;
      if (name) {
        addName(name);
        notifications.show({ color: 'teal', message: t('conditions.detected', { name }) });
      } else {
        notifications.show({ color: 'yellow', message: t('conditions.detectedNone') });
      }
      return;
    }
    const timer = setTimeout(() => setCountdown((c) => (c === null ? null : c - 1)), 1000);
    return () => clearTimeout(timer);
  }, [countdown, t]);

  return (
    <Stack gap={6}>
      <TagsInput
        label={t('conditions.programs')}
        description={t(`conditions.types.${condition.type}.description`)}
        placeholder={condition.processes.length === 0 ? t('conditions.programsPlaceholder') : undefined}
        splitChars={[',', ';']}
        clearable
        data={foreground && !has(foreground) ? [foreground] : []}
        value={condition.processes}
        onChange={(processes) => onChange({ ...condition, processes: processes.map((p) => p.trim()).filter(Boolean) })}
        error={condition.processes.length === 0 ? t('conditions.programsEmpty') : undefined}
      />
      <Group gap="xs" align="center">
        {foreground && !has(foreground) && (
          <Button size="compact-xs" variant="light" leftSection={<IconPlus size={13} />} onClick={() => add(foreground)}>
            {t('conditions.addForeground', { name: foreground })}
          </Button>
        )}
        {available ? (
          <Tooltip label={t('conditions.detectHelp')} multiline w={280}>
            <Button
              size="compact-xs"
              variant="subtle"
              leftSection={<IconFocus2 size={14} />}
              disabled={countdown !== null}
              onClick={() => setCountdown(5)}
              aria-live="polite"
            >
              {countdown === null ? t('conditions.detect') : t('conditions.detecting', { n: countdown })}
            </Button>
          </Tooltip>
        ) : (
          <Text size="xs" c="dimmed">
            {t('conditions.noForeground')}
          </Text>
        )}
      </Group>
    </Stack>
  );
}

// ---------------------------------------------------------------------------- schedule

function ScheduleEditor({ condition, onChange }: { condition: ScheduleCondition; onChange: (next: ScheduleCondition) => void }) {
  const { t, i18n } = useTranslation('profiles');
  const locale = i18n.resolvedLanguage ?? i18n.language;
  const preset = (days: readonly ScheduleCondition['days'][number][]) => onChange({ ...condition, days: [...days] });
  const wraps = wrapsPastMidnight(condition.from, condition.to);
  const same = !wraps && condition.from.slice(0, 5) === condition.to.slice(0, 5);

  return (
    <Stack gap="sm">
      <Input.Wrapper label={t('schedule.days')} error={condition.days.length === 0 ? t('schedule.noDays') : undefined}>
        <Chip.Group
          multiple
          value={condition.days}
          onChange={(value) => onChange({ ...condition, days: DAYS.filter((d) => value.includes(d)) })}
        >
          <Group gap={6} mt={6}>
            {DAYS.map((day) => (
              <Chip key={day} value={day} size="sm" aria-label={weekdayName(day, locale, 'long')}>
                {weekdayName(day, locale)}
              </Chip>
            ))}
          </Group>
        </Chip.Group>
      </Input.Wrapper>

      <Group gap={4}>
        <Button size="compact-xs" variant="subtle" onClick={() => preset(DAYS)}>
          {t('schedule.everyDay')}
        </Button>
        <Button size="compact-xs" variant="subtle" onClick={() => preset(WEEKDAYS)}>
          {t('schedule.weekdays')}
        </Button>
        <Button size="compact-xs" variant="subtle" onClick={() => preset(WEEKEND)}>
          {t('schedule.weekend')}
        </Button>
      </Group>

      <SimpleGrid cols={2} spacing="sm">
        <TimeField label={t('schedule.from')} value={condition.from} onChange={(from) => onChange({ ...condition, from })} />
        <TimeField label={t('schedule.to')} value={condition.to} onChange={(to) => onChange({ ...condition, to })} />
      </SimpleGrid>

      {same && (
        <Text size="xs" c="yellow.7">
          {t('schedule.same')}
        </Text>
      )}
      {wraps && !isScheduleEmpty(condition) && (
        <Text size="xs" c="dimmed">
          {t('schedule.wraps')}
        </Text>
      )}
    </Stack>
  );
}

// ---------------------------------------------------------------------------- sensor

function SensorEditor({ condition, onChange }: { condition: SensorCondition; onChange: (next: SensorCondition) => void }) {
  const { t, i18n } = useTranslation('profiles');
  const live = useLiveValue(condition.sensorId || undefined);
  const { byId } = useSensorLookup();
  const sensor = byId.get(condition.sensorId);
  const unit = sensor?.unit ?? '';
  const current = live?.v ?? sensor?.value;

  return (
    <Stack gap="sm">
      <SensorPicker
        label={t('sensor.sensor')}
        placeholder={t('sensor.choose')}
        value={condition.sensorId || undefined}
        onChange={(sensorId) => onChange({ ...condition, sensorId: sensorId ?? '' })}
        sensorFilter={(s) => s.valueKind === 'number'}
        error={condition.sensorId ? undefined : t('sensor.required')}
      />
      <Input.Wrapper label={t('sensor.when')}>
        <SegmentedControl
          mt={6}
          fullWidth
          value={condition.comparison}
          onChange={(comparison) => onChange({ ...condition, comparison: comparison as SensorCondition['comparison'] })}
          data={[
            { value: 'above', label: t('comparison.above') },
            { value: 'below', label: t('comparison.below') },
          ]}
        />
      </Input.Wrapper>
      <SimpleGrid cols={{ base: 1, xs: 2 }} spacing="sm">
        <NumField
          label={t('sensor.value')}
          value={condition.value}
          onCommit={(value) => onChange({ ...condition, value })}
          rightSection={
            unit ? (
              <Text size="xs" c="dimmed" pr={4}>
                {unit}
              </Text>
            ) : undefined
          }
          rightSectionWidth={unit.length > 3 ? 48 : 34}
        />
        <NumField
          label={t('sensor.forSeconds')}
          description={t('sensor.forSecondsHelp')}
          value={condition.forSeconds}
          min={0}
          max={86400}
          suffix=" s"
          onCommit={(forSeconds) => onChange({ ...condition, forSeconds })}
        />
      </SimpleGrid>
      {sensor && (
        <Text size="xs" c="dimmed">
          {t('sensor.now', {
            value: sensor.valueKind === 'number' ? formatValue(current, unit, i18n.language) : '–',
          })}
        </Text>
      )}
    </Stack>
  );
}
