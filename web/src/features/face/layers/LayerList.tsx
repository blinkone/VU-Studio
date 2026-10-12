import { useState, type DragEvent, type KeyboardEvent } from 'react';
import { ActionIcon, Menu, Text, TextInput, Tooltip } from '@mantine/core';
import {
  IconArrowDown,
  IconArrowUp,
  IconCopy,
  IconDots,
  IconEye,
  IconEyeOff,
  IconGripVertical,
  IconPencil,
  IconTrash,
} from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { FaceLayer } from '@/api/types';
import { LAYER_ICONS } from '../model/layers';
import classes from '../FaceEditor.module.css';

export type DropPosition = 'before' | 'after';

interface LayerListProps {
  /** Layers in drawing order (first = bottom); shown topmost first. */
  layers: FaceLayer[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  onToggleVisible: (id: string) => void;
  onRename: (id: string, name: string | undefined) => void;
  onDuplicate: (id: string) => void;
  onDelete: (id: string) => void;
  /** Moves a layer up (towards the top of the stack) or down. */
  onStep: (id: string, direction: 1 | -1) => void;
  /** Drops `dragId` above/below `targetId` as shown in the list. */
  onDrop: (dragId: string, targetId: string, position: DropPosition) => void;
  displayName: (layer: FaceLayer) => string;
  detail: (layer: FaceLayer) => string;
}

export function LayerList(props: LayerListProps) {
  const { layers, selectedId, onSelect, displayName, detail } = props;
  const { t } = useTranslation('face');
  const [editing, setEditing] = useState<string | null>(null);
  const [dragId, setDragId] = useState<string | null>(null);
  const [drop, setDrop] = useState<{ id: string; position: DropPosition } | null>(null);
  const shown = [...layers].reverse();

  const onDragOver = (event: DragEvent<HTMLDivElement>, id: string) => {
    if (!dragId || dragId === id) return;
    event.preventDefault();
    const rect = event.currentTarget.getBoundingClientRect();
    const position: DropPosition = event.clientY < rect.top + rect.height / 2 ? 'before' : 'after';
    setDrop((d) => (d?.id === id && d.position === position ? d : { id, position }));
  };

  const finishDrag = () => {
    setDragId(null);
    setDrop(null);
  };

  return (
    <div className={classes.layerList} role="listbox" aria-label={t('layers.title')}>
      {shown.map((layer, index) => {
        const Icon = LAYER_ICONS[layer.type];
        const selected = layer.id === selectedId;
        const name = displayName(layer);
        const isTop = index === 0;
        const isBottom = index === shown.length - 1;
        return (
          <div
            key={layer.id}
            className={classes.layerRow}
            data-selected={selected || undefined}
            data-hidden={!layer.visible || undefined}
            data-drop={drop?.id === layer.id ? drop.position : undefined}
            role="option"
            data-layer-row
            aria-selected={selected}
            tabIndex={0}
            draggable={editing !== layer.id}
            onClick={() => onSelect(layer.id)}
            onDoubleClick={() => setEditing(layer.id)}
            onKeyDown={(e: KeyboardEvent<HTMLDivElement>) => {
              if (e.target !== e.currentTarget) return;
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                onSelect(layer.id);
              } else if (e.key === 'F2') {
                setEditing(layer.id);
              }
            }}
            onDragStart={(e) => {
              setDragId(layer.id);
              e.dataTransfer.effectAllowed = 'move';
              e.dataTransfer.setData('text/plain', layer.id);
            }}
            onDragOver={(e) => onDragOver(e, layer.id)}
            onDragLeave={() => setDrop((d) => (d?.id === layer.id ? null : d))}
            onDrop={(e) => {
              e.preventDefault();
              if (dragId && drop) props.onDrop(dragId, drop.id, drop.position);
              finishDrag();
            }}
            onDragEnd={finishDrag}
          >
            <span className={classes.layerGrip} aria-hidden>
              <IconGripVertical size={14} />
            </span>
            <Icon size={16} stroke={1.7} style={{ flex: 'none', opacity: layer.visible ? 0.9 : 0.4 }} />
            <div className={classes.layerName}>
              {editing === layer.id ? (
                <TextInput
                  size="xs"
                  autoFocus
                  defaultValue={layer.name ?? ''}
                  placeholder={name}
                  aria-label={t('layers.rename')}
                  onClick={(e) => e.stopPropagation()}
                  onBlur={(e) => {
                    props.onRename(layer.id, e.currentTarget.value.trim() || undefined);
                    setEditing(null);
                  }}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') e.currentTarget.blur();
                    if (e.key === 'Escape') setEditing(null);
                    e.stopPropagation();
                  }}
                />
              ) : (
                <>
                  <Text size="sm" fw={selected ? 600 : 500} truncate>
                    {name}
                  </Text>
                  <Text size="xs" c="dimmed" truncate ff={layer.type === 'text' ? 'monospace' : undefined}>
                    {detail(layer)}
                  </Text>
                </>
              )}
            </div>
            <div className={classes.layerActions}>
              <Tooltip label={layer.visible ? t('layers.hide') : t('layers.show')}>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="sm"
                  aria-label={layer.visible ? t('layers.hide') : t('layers.show')}
                  aria-pressed={!layer.visible}
                  onClick={(e) => {
                    e.stopPropagation();
                    props.onToggleVisible(layer.id);
                  }}
                >
                  {layer.visible ? <IconEye size={15} /> : <IconEyeOff size={15} />}
                </ActionIcon>
              </Tooltip>
              <Menu position="bottom-end" withinPortal shadow="md" width={190}>
                <Menu.Target>
                  <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('layers.more')} onClick={(e) => e.stopPropagation()}>
                    <IconDots size={15} />
                  </ActionIcon>
                </Menu.Target>
                <Menu.Dropdown onClick={(e) => e.stopPropagation()}>
                  <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => setEditing(layer.id)}>
                    {t('layers.rename')}
                  </Menu.Item>
                  <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => props.onDuplicate(layer.id)} rightSection={<Text size="xs" c="dimmed">Ctrl+D</Text>}>
                    {t('layers.duplicate')}
                  </Menu.Item>
                  <Menu.Item leftSection={<IconArrowUp size={14} />} disabled={isTop} onClick={() => props.onStep(layer.id, 1)}>
                    {t('layers.moveUp')}
                  </Menu.Item>
                  <Menu.Item leftSection={<IconArrowDown size={14} />} disabled={isBottom} onClick={() => props.onStep(layer.id, -1)}>
                    {t('layers.moveDown')}
                  </Menu.Item>
                  <Menu.Divider />
                  <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={() => props.onDelete(layer.id)} rightSection={<Text size="xs" c="dimmed">Del</Text>}>
                    {t('layers.delete')}
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            </div>
          </div>
        );
      })}
    </div>
  );
}
