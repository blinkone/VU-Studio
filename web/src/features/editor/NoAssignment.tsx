import { Paper, SimpleGrid, Stack, Text, ThemeIcon, Title, UnstyledButton } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconCopy, IconFilePlus, IconPlugConnectedX } from '@tabler/icons-react';
import { cloneAssignment, createAssignment } from './defaults';
import type { DialEditorModel } from './useDialEditor';
import classes from './editor.module.css';

/** Shown when the chosen profile has nothing for this slot: offers to create the assignment. */
export function NoAssignment({ model }: { model: DialEditorModel }) {
  const { t } = useTranslation('editor');
  const { profile, defaultProfile, slot } = model;
  const source = !model.isDefaultProfile ? defaultProfile?.dials.find((d) => d.slot === slot && d.pages.length > 0) : undefined;
  const inherits = !model.isDefaultProfile && profile.inheritUnassigned && !!source;

  const create = (copy: boolean) => {
    const assignment = copy && source ? cloneAssignment(source, slot) : createAssignment(slot);
    model.updateProfile((p) => {
      p.dials = [...p.dials.filter((d) => d.slot !== slot), assignment].sort((a, b) => a.slot - b.slot);
    });
    model.setPage(assignment.pages[0].id);
  };

  const choices = [
    ...(source
      ? [{ key: 'copy', icon: <IconCopy size={22} />, title: t('create.copy', { name: defaultProfile!.name }), text: t('create.copyHint'), onClick: () => create(true) }]
      : []),
    { key: 'empty', icon: <IconFilePlus size={22} />, title: t('create.empty'), text: t('create.emptyHint'), onClick: () => create(false) },
  ];

  return (
    <Paper withBorder p={{ base: 'lg', sm: 'xl' }} className={classes.section}>
      <Stack gap="lg" align="center" ta="center">
        <ThemeIcon size={56} radius="xl" variant="light" color="gray">
          <IconPlugConnectedX size={28} stroke={1.5} />
        </ThemeIcon>
        <Stack gap={6} maw={560}>
          <Title order={2} size="h3">
            {t('create.title', { slot, profile: profile.name })}
          </Title>
          <Text c="dimmed" size="sm">
            {model.isDefaultProfile ? t('create.idleDefault') : inherits ? t('create.inherit', { name: defaultProfile!.name }) : t('create.idle')}
          </Text>
        </Stack>
        <SimpleGrid cols={{ base: 1, sm: choices.length }} spacing="md" w="100%" maw={choices.length * 300}>
          {choices.map((c) => (
            <UnstyledButton key={c.key} onClick={c.onClick} className={classes.option} p="md" ta="left" style={{ border: '1px solid var(--mantine-color-default-border)' }}>
              <Stack gap={8}>
                <ThemeIcon variant="light" size={40} radius="md">
                  {c.icon}
                </ThemeIcon>
                <Text fw={600}>{c.title}</Text>
                <Text size="sm" c="dimmed">
                  {c.text}
                </Text>
              </Stack>
            </UnstyledButton>
          ))}
        </SimpleGrid>
      </Stack>
    </Paper>
  );
}
