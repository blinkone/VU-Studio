import { Fragment } from 'react';
import { Badge, Group, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconAppWindow, IconApps, IconCalendarTime, IconGauge, type Icon } from '@tabler/icons-react';
import type { Profile } from '@/api/types';
import { useSummaryContext } from './hooks';
import { summarizeCondition, type ConditionType } from './profileModel';

export const CONDITION_ICONS: Record<ConditionType, Icon> = {
  processRunning: IconApps,
  foregroundApp: IconAppWindow,
  schedule: IconCalendarTime,
  sensor: IconGauge,
};

/** One-line, human-readable description of when a profile activates. */
export function ConditionSummary({ profile }: { profile: Profile }) {
  const { t } = useTranslation('profiles');
  const ctx = useSummaryContext();

  if (profile.activation === 'always') {
    return (
      <Text size="sm" c="dimmed">
        {t('summary.always')}
      </Text>
    );
  }
  if (profile.activation === 'manual') {
    return (
      <Text size="sm" c="dimmed">
        {t('summary.manual')}
      </Text>
    );
  }
  if (profile.conditions.length === 0) {
    return (
      <Text size="sm" c="yellow.7">
        {t('summary.noConditions')}
      </Text>
    );
  }

  const separator = profile.match === 'all' ? t('summary.and') : t('summary.or');
  return (
    <Group gap={6} wrap="wrap" align="center">
      {profile.conditions.map((condition, index) => {
        const summary = summarizeCondition(condition, ctx);
        const Glyph = CONDITION_ICONS[condition.type];
        return (
          <Fragment key={index}>
            {index > 0 && (
              <Text size="xs" c="dimmed" fw={600}>
                {separator}
              </Text>
            )}
            <Badge
              variant="light"
              color={summary.incomplete ? 'yellow' : 'gray'}
              size="lg"
              maw="100%"
              leftSection={<Glyph size={14} stroke={1.8} />}
              styles={{ label: { textTransform: 'none', fontWeight: 500, fontSize: 'var(--mantine-font-size-xs)' } }}
            >
              {summary.text}
            </Badge>
          </Fragment>
        );
      })}
    </Group>
  );
}
