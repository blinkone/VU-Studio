import { useState } from 'react';
import { ActionIcon, Button, Group, NumberInput, Paper, Popover, Stack, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import {
  IconArrowDown,
  IconArrowUp,
  IconCopy,
  IconLayersSubtract,
  IconPencil,
  IconPlus,
  IconRotateClockwise2,
  IconTrash,
  IconUnlink,
} from '@tabler/icons-react';
import { useSensors, useThemes } from '@/api/queries';
import type { DialPage } from '@/api/types';
import { rgbwPreview, gradientCss } from '@/features/dial/color';
import { clonePage, createPage } from './defaults';
import { Section } from './Section';
import { pageLabel, themeLabel } from './pageLabels';
import type { DialEditorModel } from './useDialEditor';
import classes from './editor.module.css';

const toNumber = (v: string | number): number | undefined => (v === '' ? undefined : typeof v === 'number' ? v : Number.isFinite(Number(v)) ? Number(v) : undefined);

/** Pages of this dial in the chosen profile: order, names, rotation. */
export function PagesTab({ model }: { model: DialEditorModel }) {
  const { t, i18n } = useTranslation('editor');
  const { data: sensors } = useSensors();
  const { data: themes } = useThemes();
  const assignment = model.assignment!;
  const pages = assignment.pages;

  const move = (index: number, delta: -1 | 1) =>
    model.updateAssignment((a) => {
      const target = index + delta;
      if (target < 0 || target >= a.pages.length) return;
      const [page] = a.pages.splice(index, 1);
      a.pages.splice(target, 0, page);
    });

  const add = () => {
    const page = createPage();
    model.updateAssignment((a) => {
      a.pages.push(page);
    });
    model.setPage(page.id);
  };

  const duplicate = (index: number) => {
    const copy = clonePage(pages[index]);
    if (copy.name) copy.name = t('pages.copyName', { name: copy.name });
    model.updateAssignment((a) => {
      a.pages.splice(index + 1, 0, copy);
    });
    model.setPage(copy.id);
  };

  const remove = (index: number) => {
    const removed = pages[index];
    model.updateAssignment((a) => {
      if (a.pages.length > 1) a.pages.splice(index, 1);
    });
    if (removed.id === model.page?.id) {
      const next = pages[index + 1] ?? pages[index - 1];
      if (next) model.setPage(next.id);
    }
  };

  return (
    <Stack gap="lg">
      <Section icon={<IconRotateClockwise2 size={18} />} title={t('pages.rotation')} description={t('pages.rotationHint')}>
        <Group align="flex-end" gap="md" wrap="wrap">
          <NumberInput
            label={t('pages.rotateEvery')}
            w={200}
            min={0}
            max={86400}
            allowDecimal={false}
            suffix=" s"
            value={assignment.rotationSeconds}
            onChange={(v) =>
              model.updateAssignment((a) => {
                a.rotationSeconds = Math.max(0, Math.round(toNumber(v) ?? 0));
              })
            }
          />
          <Text size="xs" c="dimmed" maw={420} pb={8}>
            {assignment.rotationSeconds === 0 ? t('pages.noRotation') : pages.length < 2 ? t('pages.singlePage') : t('pages.rotationOn', { count: pages.length })}
          </Text>
        </Group>
      </Section>

      <Section
        icon={<IconLayersSubtract size={18} />}
        title={t('pages.list')}
        description={t('pages.listHint')}
        aside={
          <Button size="xs" variant="light" leftSection={<IconPlus size={14} />} onClick={add}>
            {t('pages.add')}
          </Button>
        }
      >
        <Stack gap="sm" component="ol" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
          {pages.map((page, index) => (
            <PageRow
              key={page.id}
              page={page}
              index={index}
              count={pages.length}
              selected={page.id === model.page?.id}
              label={pageLabel(page, index, sensors, t)}
              sensorName={sensors?.find((s) => s.id === page.value.sensorId)?.name ?? page.value.sensorId}
              theme={page.face.custom ? t('pages.customFace') : themeLabel(page.face.theme, themes, i18n.language)}
              rotationSeconds={assignment.rotationSeconds}
              onSelect={() => model.setPage(page.id)}
              onEdit={() => {
                model.setPage(page.id);
                model.setTab('data');
              }}
              onRename={(name) =>
                model.updateAssignment((a) => {
                  a.pages[index].name = name || undefined;
                })
              }
              onDuration={(seconds) =>
                model.updateAssignment((a) => {
                  a.pages[index].durationSeconds = seconds;
                })
              }
              onMove={(delta) => move(index, delta)}
              onDuplicate={() => duplicate(index)}
              onDelete={() => remove(index)}
            />
          ))}
        </Stack>
      </Section>

      <RemoveAssignment model={model} />
    </Stack>
  );
}

interface PageRowProps {
  page: DialPage;
  index: number;
  count: number;
  selected: boolean;
  label: string;
  sensorName: string | undefined;
  theme: string;
  rotationSeconds: number;
  onSelect: () => void;
  onEdit: () => void;
  onRename: (name: string) => void;
  onDuration: (seconds: number | undefined) => void;
  onMove: (delta: -1 | 1) => void;
  onDuplicate: () => void;
  onDelete: () => void;
}

function PageRow(props: PageRowProps) {
  const { page, index, count, selected, label, sensorName, theme, rotationSeconds } = props;
  const { t } = useTranslation('editor');
  const light = page.light;
  const lightCss =
    light.mode === 'off' ? 'transparent' : light.mode === 'gradient' ? gradientCss(light.gradient, '135deg') : rgbwPreview(light.color);

  return (
    <Paper component="li" withBorder p="sm" className={`${classes.row} ${classes.pageRow}`} data-selected={selected || undefined}>
      <Group gap="sm" wrap="nowrap" align="flex-start">
        <UnstyledButton onClick={props.onSelect} aria-label={t('pages.select', { name: label })} aria-current={selected || undefined} mt={4}>
          <span className={classes.pageIndex}>{index + 1}</span>
        </UnstyledButton>
        <Stack gap={6} style={{ flex: 1, minWidth: 0 }}>
          <Group gap="sm" wrap="wrap" align="flex-end">
            <TextInput
              size="sm"
              style={{ flex: '1 1 180px' }}
              label={t('pages.name')}
              placeholder={label}
              value={page.name ?? ''}
              onChange={(e) => props.onRename(e.currentTarget.value)}
              maxLength={40}
            />
            <NumberInput
              size="sm"
              w={150}
              label={t('pages.duration')}
              placeholder={t('pages.durationDefault', { seconds: rotationSeconds })}
              min={1}
              max={86400}
              allowDecimal={false}
              suffix=" s"
              value={page.durationSeconds ?? ''}
              onChange={(v) => props.onDuration(toNumber(v))}
              disabled={count < 2}
            />
          </Group>
          <Group gap="xs" wrap="wrap">
            <span className={classes.pageSwatch} style={{ background: lightCss }} aria-hidden />
            <Text size="xs" c="dimmed">
              {sensorName ?? t('pages.noSensor')} · {theme}
            </Text>
          </Group>
        </Stack>
        <Group gap={2} wrap="nowrap" mt={22}>
          <Tooltip label={t('pages.edit')}>
            <ActionIcon variant="subtle" color="gray" onClick={props.onEdit} aria-label={t('pages.edit')}>
              <IconPencil size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('pages.moveUp')}>
            <ActionIcon variant="subtle" color="gray" disabled={index === 0} onClick={() => props.onMove(-1)} aria-label={t('pages.moveUp')}>
              <IconArrowUp size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('pages.moveDown')}>
            <ActionIcon variant="subtle" color="gray" disabled={index === count - 1} onClick={() => props.onMove(1)} aria-label={t('pages.moveDown')}>
              <IconArrowDown size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('pages.duplicate')}>
            <ActionIcon variant="subtle" color="gray" onClick={props.onDuplicate} aria-label={t('pages.duplicate')}>
              <IconCopy size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label={count <= 1 ? t('pages.lastPage') : t('pages.delete')}>
            <span>
              <ActionIcon variant="subtle" color="red" disabled={count <= 1} onClick={props.onDelete} aria-label={t('pages.delete')}>
                <IconTrash size={16} />
              </ActionIcon>
            </span>
          </Tooltip>
        </Group>
      </Group>
    </Paper>
  );
}

/** Removes this slot's assignment from the profile (it then inherits or goes idle). */
function RemoveAssignment({ model }: { model: DialEditorModel }) {
  const { t } = useTranslation('editor');
  const [open, setOpen] = useState(false);
  const inherits = !model.isDefaultProfile && model.profile.inheritUnassigned && !!model.defaultProfile;

  return (
    <Paper withBorder p={{ base: 'md', sm: 'lg' }} className={classes.section}>
      <Group justify="space-between" gap="md" wrap="wrap">
        <div style={{ flex: '1 1 260px' }}>
          <Text size="sm" fw={600}>
            {t('pages.removeAssignment', { profile: model.profile.name })}
          </Text>
          <Text size="xs" c="dimmed">
            {inherits ? t('pages.removeInherit', { name: model.defaultProfile!.name }) : t('pages.removeIdle')}
          </Text>
        </div>
        <Popover opened={open} onChange={setOpen} position="bottom-end" withArrow shadow="md">
          <Popover.Target>
            <Button variant="default" color="red" leftSection={<IconUnlink size={16} />} onClick={() => setOpen((o) => !o)}>
              {t('pages.remove')}
            </Button>
          </Popover.Target>
          <Popover.Dropdown maw={280}>
            <Text size="sm" mb="sm">
              {t('pages.removeConfirm', { count: model.assignment?.pages.length ?? 0 })}
            </Text>
            <Group justify="flex-end" gap="xs">
              <Button size="xs" variant="default" onClick={() => setOpen(false)}>
                {t('common:actions.cancel')}
              </Button>
              <Button
                size="xs"
                color="red"
                onClick={() => {
                  setOpen(false);
                  model.updateProfile((p) => {
                    p.dials = p.dials.filter((d) => d.slot !== model.slot);
                  });
                }}
              >
                {t('pages.remove')}
              </Button>
            </Group>
          </Popover.Dropdown>
        </Popover>
      </Group>
    </Paper>
  );
}
