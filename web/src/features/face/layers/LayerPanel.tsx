import { ActionIcon, Button, Divider, Group, Kbd, Menu, Paper, ScrollArea, Stack, Text, TextInput, ThemeIcon, Tooltip } from '@mantine/core';
import { IconEye, IconEyeOff, IconLayersSubtract, IconPlus, IconTrash } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { FaceDocument, FaceLayer, Ink } from '@/api/types';
import { SegmentField } from '../components/fields';
import { LAYER_ICONS, LAYER_TYPES, type LayerType } from '../model/layers';
import { LayerList, type DropPosition } from './LayerList';
import { ImageProps } from './props/ImageProps';
import { BarProps, IconProps, ShapeProps, SparklineProps } from './props/OtherProps';
import { ScaleProps } from './props/ScaleProps';
import { TextProps } from './props/TextProps';
import type { Patch } from './props/shared';

export interface LayerOps {
  add: (type: LayerType) => void;
  patch: (id: string, patch: Partial<FaceLayer>, coalesce?: string) => void;
  remove: (id: string) => void;
  duplicate: (id: string) => void;
  step: (id: string, direction: 1 | -1) => void;
  drop: (dragId: string, targetId: string, position: DropPosition) => void;
}

interface LayerPanelProps {
  doc: FaceDocument;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  ops: LayerOps;
  displayName: (layer: FaceLayer) => string;
  detail: (layer: FaceLayer) => string;
  pageIcon: string | undefined;
}

/** Layer stack plus the property panel of the selected layer. */
export function LayerPanel({ doc, selectedId, onSelect, ops, displayName, detail, pageIcon }: LayerPanelProps) {
  const { t } = useTranslation('face');
  const selected = doc.layers.find((l) => l.id === selectedId) ?? null;

  const addMenu = (
    <Menu position="bottom-end" shadow="md" width={210} withinPortal>
      <Menu.Target>
        <Button size="compact-sm" variant="light" leftSection={<IconPlus size={14} />}>
          {t('layers.add')}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        {LAYER_TYPES.map((type) => {
          const Icon = LAYER_ICONS[type];
          return (
            <Menu.Item key={type} leftSection={<Icon size={16} stroke={1.7} />} onClick={() => ops.add(type)}>
              <Text size="sm">{t(`layerTypes.${type}`)}</Text>
              <Text size="xs" c="dimmed">
                {t(`layerHints.${type}`)}
              </Text>
            </Menu.Item>
          );
        })}
      </Menu.Dropdown>
    </Menu>
  );

  return (
    <Stack gap="md">
      <Paper withBorder radius="md" p={6}>
        <Group justify="space-between" px={6} pt={2} pb={6}>
          <Text fw={650} size="sm">
            {t('layers.title')}{' '}
            <Text span c="dimmed" size="xs">
              {doc.layers.length}
            </Text>
          </Text>
          {addMenu}
        </Group>
        {doc.layers.length === 0 ? (
          <Stack align="center" gap={6} py="lg" px="md" ta="center">
            <ThemeIcon variant="light" size={40} radius="xl" color="gray">
              <IconLayersSubtract size={22} />
            </ThemeIcon>
            <Text size="sm" fw={600}>
              {t('layers.emptyTitle')}
            </Text>
            <Text size="xs" c="dimmed">
              {t('layers.emptyText')}
            </Text>
          </Stack>
        ) : (
          <ScrollArea.Autosize mah={300} type="auto" offsetScrollbars="y">
            <LayerList
              layers={doc.layers}
              selectedId={selectedId}
              onSelect={onSelect}
              onToggleVisible={(id) => {
                const layer = doc.layers.find((l) => l.id === id);
                if (layer) ops.patch(id, { visible: !layer.visible });
              }}
              onRename={(id, name) => ops.patch(id, { name })}
              onDuplicate={ops.duplicate}
              onDelete={ops.remove}
              onStep={ops.step}
              onDrop={ops.drop}
              displayName={displayName}
              detail={detail}
            />
          </ScrollArea.Autosize>
        )}
      </Paper>

      {selected ? (
        <LayerProperties key={selected.id} layer={selected} ops={ops} displayName={displayName(selected)} pageIcon={pageIcon} />
      ) : (
        doc.layers.length > 0 && (
          <Text size="xs" c="dimmed" ta="center" px="md">
            {t('layers.selectHint')}
          </Text>
        )
      )}

      <Group gap={6} justify="center" c="dimmed" wrap="wrap">
        <Text size="xs" c="dimmed">
          <Kbd size="xs">←↑→↓</Kbd> {t('shortcuts.nudge')} · <Kbd size="xs">Shift</Kbd> ×10 · <Kbd size="xs">Del</Kbd> {t('shortcuts.delete')} ·{' '}
          <Kbd size="xs">Ctrl Z</Kbd> {t('shortcuts.undo')}
        </Text>
      </Group>
    </Stack>
  );
}

function patcher<T extends FaceLayer>(ops: LayerOps, id: string): Patch<T> {
  return (p, coalesce) => ops.patch(id, p, coalesce);
}

function LayerProperties({ layer, ops, displayName, pageIcon }: { layer: FaceLayer; ops: LayerOps; displayName: string; pageIcon: string | undefined }) {
  const { t } = useTranslation('face');
  const Icon = LAYER_ICONS[layer.type];

  return (
    <Paper withBorder radius="md" p="sm">
      <Stack gap="md">
        <Group gap="xs" wrap="nowrap">
          <ThemeIcon variant="light" size="md" radius="md">
            <Icon size={16} />
          </ThemeIcon>
          <TextInput
            size="xs"
            style={{ flex: 1 }}
            value={layer.name ?? ''}
            placeholder={displayName}
            aria-label={t('layers.name')}
            onChange={(e) => ops.patch(layer.id, { name: e.currentTarget.value || undefined }, `${layer.id}:name`)}
          />
          <Tooltip label={layer.visible ? t('layers.hide') : t('layers.show')}>
            <ActionIcon variant="subtle" color="gray" aria-label={layer.visible ? t('layers.hide') : t('layers.show')} onClick={() => ops.patch(layer.id, { visible: !layer.visible })}>
              {layer.visible ? <IconEye size={16} /> : <IconEyeOff size={16} />}
            </ActionIcon>
          </Tooltip>
          <Tooltip label={t('layers.delete')}>
            <ActionIcon variant="subtle" color="red" aria-label={t('layers.delete')} onClick={() => ops.remove(layer.id)}>
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
        </Group>

        <SegmentField<Ink>
          label={t('props.ink')}
          value={layer.ink}
          onChange={(ink) => ops.patch(layer.id, { ink })}
          data={[
            { value: 'foreground', label: t('inks.foreground') },
            { value: 'background', label: t('inks.background') },
          ]}
        />

        <Divider />

        {layer.type === 'text' && <TextProps layer={layer} patch={patcher(ops, layer.id)} />}
        {layer.type === 'icon' && <IconProps layer={layer} patch={patcher(ops, layer.id)} pageIcon={pageIcon} />}
        {layer.type === 'scale' && <ScaleProps layer={layer} patch={patcher(ops, layer.id)} />}
        {layer.type === 'image' && <ImageProps layer={layer} patch={patcher(ops, layer.id)} />}
        {layer.type === 'shape' && <ShapeProps layer={layer} patch={patcher(ops, layer.id)} />}
        {layer.type === 'bar' && <BarProps layer={layer} patch={patcher(ops, layer.id)} />}
        {layer.type === 'sparkline' && <SparklineProps layer={layer} patch={patcher(ops, layer.id)} />}
      </Stack>
    </Paper>
  );
}

