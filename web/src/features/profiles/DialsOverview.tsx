import { useMemo } from 'react';
import { Badge, Button, Group, Stack, Switch, Text, ThemeIcon } from '@mantine/core';
import { Link } from 'react-router';
import { useTranslation } from 'react-i18next';
import { IconCornerLeftDown, IconGauge, IconPencil, IconPlus } from '@tabler/icons-react';
import { useThemes } from '@/api/queries';
import type { DialAssignment, DialSettings, Profile } from '@/api/types';
import { localized } from '@/i18n';
import { useSensorLookup } from './hooks';
import { findDefaultProfile, hasPages } from './profileModel';
import classes from './profiles.module.css';

interface DialsOverviewProps {
  profile: Profile;
  profiles: readonly Profile[];
  dials: readonly DialSettings[];
  onInheritChange: (inherit: boolean) => void;
}

interface SlotRow {
  slot: number;
  name?: string;
  kind: 'own' | 'inherited' | 'none';
  assignment?: DialAssignment;
}

/** Per-dial overview of what this profile shows, with a link to the dial editor for each slot. */
export function DialsOverview({ profile, profiles, dials, onInheritChange }: DialsOverviewProps) {
  const { t, i18n } = useTranslation('profiles');
  const { byId } = useSensorLookup();
  const { data: themes } = useThemes();
  const defaultProfile = findDefaultProfile(profiles);
  const inheritFrom = defaultProfile && defaultProfile.id !== profile.id ? defaultProfile : undefined;

  const rows = useMemo<SlotRow[]>(() => {
    const slots = new Set<number>();
    for (const d of dials) slots.add(d.slot);
    for (const a of profile.dials) slots.add(a.slot);
    for (const a of defaultProfile?.dials ?? []) slots.add(a.slot);
    return [...slots]
      .filter((slot) => slot > 0)
      .sort((a, b) => a - b)
      .map((slot): SlotRow => {
        const name = dials.find((d) => d.slot === slot)?.name;
        const own = profile.dials.find((a) => a.slot === slot);
        if (hasPages(own)) return { slot, name, kind: 'own', assignment: own };
        const inherited = profile.inheritUnassigned ? inheritFrom?.dials.find((a) => a.slot === slot) : undefined;
        if (hasPages(inherited)) return { slot, name, kind: 'inherited', assignment: inherited };
        return { slot, name, kind: 'none' };
      });
  }, [dials, profile.dials, profile.inheritUnassigned, defaultProfile, inheritFrom]);

  const describe = (assignment: DialAssignment) => {
    const page = assignment.pages[0];
    const sensorId = page.value.sensorId;
    const sensor = sensorId ? (byId.get(sensorId)?.name ?? sensorId) : undefined;
    const title = sensor ?? page.value.label ?? page.name ?? t('dials.noSensor');
    const theme = page.face.custom ? t('dials.customFace') : (localized(themes?.find((x) => x.id === page.face.theme)?.name, i18n.language) || page.face.theme);
    const pages = t('dials.pages', { count: assignment.pages.length });
    return { title, detail: `${theme} · ${pages}` };
  };

  return (
    <Stack gap="md">
      <Switch
        checked={profile.inheritUnassigned}
        onChange={(event) => onInheritChange(event.currentTarget.checked)}
        label={t('editor.inherit')}
        description={t('editor.inheritHelp', { name: inheritFrom?.name ?? t('dials.defaultProfile') })}
      />

      {rows.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('dials.empty')}
        </Text>
      ) : (
        <div>
          {rows.map((row) => {
            const info = row.assignment ? describe(row.assignment) : undefined;
            const href = `/dials/${row.slot}?profile=${encodeURIComponent(profile.id)}`;
            return (
              <Group key={row.slot} className={classes.slotRow} py="sm" gap="sm" wrap="nowrap" align="center">
                <ThemeIcon variant="light" color={row.kind === 'none' ? 'gray' : undefined} size={36} radius="md">
                  <Text fw={700} size="sm">
                    {row.slot}
                  </Text>
                </ThemeIcon>
                <Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
                  <Group gap={6} wrap="nowrap">
                    <Text size="sm" fw={600} truncate>
                      {row.name ?? t('dials.slot', { slot: row.slot })}
                    </Text>
                    {row.kind === 'inherited' && (
                      <Badge size="sm" variant="light" color="gray" leftSection={<IconCornerLeftDown size={12} />}>
                        {t('dials.inherited')}
                      </Badge>
                    )}
                  </Group>
                  {info ? (
                    <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
                      <IconGauge size={14} aria-hidden style={{ flex: 'none', opacity: 0.6 }} />
                      <Text size="xs" c="dimmed" truncate>
                        <Text span fw={500} c="inherit" inherit>
                          {info.title}
                        </Text>
                        {' · '}
                        {info.detail}
                      </Text>
                    </Group>
                  ) : (
                    <Text size="xs" c="dimmed">
                      {profile.inheritUnassigned && inheritFrom ? t('dials.noneInherit') : t('dials.none')}
                    </Text>
                  )}
                  {row.kind === 'inherited' && inheritFrom && (
                    <Text size="xs" c="dimmed" truncate>
                      {t('dials.inheritsFrom', { name: inheritFrom.name })}
                    </Text>
                  )}
                </Stack>
                <Button
                  size="xs"
                  variant={row.kind === 'own' ? 'default' : 'light'}
                  component={Link}
                  to={href}
                  leftSection={row.kind === 'own' ? <IconPencil size={14} /> : <IconPlus size={14} />}
                  aria-label={`${row.kind === 'own' ? t('dials.edit') : t('dials.assign')}: ${row.name ?? t('dials.slot', { slot: row.slot })}`}
                >
                  {row.kind === 'own' ? t('dials.edit') : t('dials.assign')}
                </Button>
              </Group>
            );
          })}
        </div>
      )}
    </Stack>
  );
}
