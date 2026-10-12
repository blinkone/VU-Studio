import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ActionIcon, Badge, Button, Group, Stack, Tabs, Text, ThemeIcon, Tooltip } from '@mantine/core';
import { useLocalStorage } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import {
  IconAdjustments,
  IconArrowBackUp,
  IconArrowForwardUp,
  IconLayersSubtract,
  IconPalette,
  IconPencil,
  IconRestore,
  IconTarget,
} from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { api } from '@/api/client';
import { useThemes } from '@/api/queries';
import type { DialPage, FaceDocument, FaceLayer } from '@/api/types';
import { localized } from '@/i18n';
import { useConfigDraft } from '@/state/configDraft';
import { OptionsPanel } from './components/OptionsPanel';
import { StageArea, type NeedleState } from './components/StageArea';
import { ThemeGallery } from './components/ThemeGallery';
import { useConfirm } from './components/useConfirm';
import { LayerCanvas, shiftScale } from './layers/LayerCanvas';
import { LayerPanel, type LayerOps } from './layers/LayerPanel';
import { DEFAULT_GEOMETRY } from './model/geometry';
import { useFaceHistory, type EditOptions } from './model/history';
import { createLayer, duplicateLayer, normalizeDocument, offsetLayer, placeNewLayer, emptyDocument } from './model/layers';
import { usePageInfo } from './model/sample';
import { useLivePreview } from './preview/useLivePreview';
import classes from './FaceEditor.module.css';

export interface FaceEditorProps {
  /** Slot the page belongs to (used for live preview context). */
  slot: number;
  /** The page being edited; only `page.face` is changed by this editor. */
  page: DialPage;
  /** Called with the new face configuration. */
  onChange: (face: DialPage['face']) => void;
}

type Tab = 'theme' | 'options' | 'layers';

/** Names the built-in themes give their layers; shown translated until the user renames them. */
const GENERATED_NAMES = new Set(['Title', 'Subtitle', 'Value', 'Unit', 'Icon', 'Scale', 'History', 'Line', 'Box', 'Caption', 'Bar', 'Frame', 'Clock']);

/**
 * Face editor: theme gallery, options, a large live preview in a backlit bezel, and a visual layer
 * editor for custom layouts. Works on a local copy of `page.face` (with undo/redo) and reports changes
 * through `onChange`, debounced.
 */
export function FaceEditor(props: FaceEditorProps) {
  // A different page starts a fresh editor (history, selection, needle).
  return <FaceEditorInner key={`${props.slot}:${props.page.id}`} {...props} />;
}

function isEditable(target: EventTarget | null): boolean {
  return (
    target instanceof Element &&
    target.closest(
      'input, textarea, select, [contenteditable="true"], [role="slider"], [role="listbox"] input, [role="menu"], [role="combobox"], [role="option"]:not([data-layer-row]), [role="menuitem"], [role="dialog"]',
    ) !== null
  );
}

function FaceEditorInner({ slot, page, onChange }: FaceEditorProps) {
  const { t, i18n } = useTranslation('face');
  const history = useFaceHistory(page.face, onChange);
  const { face, update } = history;
  const doc = face.custom;
  const previewPage = useMemo(() => ({ ...page, face }), [page, face]);
  const preview = useLivePreview(slot, previewPage, 4);
  const themesQuery = useThemes();
  const theme = themesQuery.data?.find((th) => th.id === face.theme);
  const info = usePageInfo(slot, page, face);
  const geometry = useConfigDraft((s) => s.draft?.hardware.geometry) ?? DEFAULT_GEOMETRY;
  const [tab, setTab] = useState<Tab>(doc ? 'layers' : 'theme');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [needle, setNeedle] = useState<NeedleState>({ visible: true, follow: true, value: 0.62 });
  const [showSweep, setShowSweep] = useLocalStorage({ key: 'vustudio.face.sweepGuide', defaultValue: true });
  const [customizing, setCustomizing] = useState(false);
  const [confirmDialog, confirm] = useConfirm();
  const rootRef = useRef<HTMLDivElement>(null);
  const pointerInside = useRef(false);

  const selected = doc?.layers.find((l) => l.id === selectedId) ?? null;

  // ------------------------------------------------------------ face-level actions

  const selectTheme = async (themeId: string) => {
    if (face.custom) {
      const ok = await confirm({
        title: t('confirm.replaceTitle'),
        message: t('confirm.replaceMessage'),
        confirmLabel: t('confirm.replaceAction'),
        danger: true,
      });
      if (!ok) return;
      setSelectedId(null);
    } else if (themeId === face.theme) {
      return;
    }
    update((f) => ({ ...f, theme: themeId, custom: undefined }));
  };

  const customize = async () => {
    setCustomizing(true);
    try {
      const generated = await api.post<FaceDocument>('/api/faces/document', { slot, page: { ...previewPage, face: { ...face, custom: undefined } } });
      const document = normalizeDocument(generated && Array.isArray(generated.layers) ? generated : emptyDocument(face.inverted));
      update((f) => ({ ...f, custom: document }));
      setSelectedId(null);
      setTab('layers');
      if (document.layers.length === 0) {
        notifications.show({ color: 'yellow', title: t('customize.emptyTitle'), message: t('customize.emptyText') });
      }
    } catch (error) {
      notifications.show({ color: 'red', title: t('customize.failed'), message: error instanceof Error ? error.message : String(error) });
    } finally {
      setCustomizing(false);
    }
  };

  const backToTheme = async () => {
    const ok = await confirm({
      title: t('confirm.backTitle'),
      message: t('confirm.backMessage'),
      confirmLabel: t('confirm.backAction'),
      danger: true,
    });
    if (!ok) return;
    setSelectedId(null);
    update((f) => ({ ...f, custom: undefined }));
    setTab('theme');
  };

  // ------------------------------------------------------------ layer operations

  const updateDoc = useCallback(
    (recipe: (doc: FaceDocument) => FaceDocument, options?: EditOptions) =>
      update((f) => (f.custom ? { ...f, custom: recipe(f.custom) } : f), options),
    [update],
  );

  const replaceLayer = useCallback(
    (layer: FaceLayer) => updateDoc((d) => ({ ...d, layers: d.layers.map((l) => (l.id === layer.id ? layer : l)) })),
    [updateDoc],
  );

  const ops = useMemo<LayerOps>(
    () => ({
      add: (type) => {
        let created: FaceLayer | null = null;
        updateDoc((d) => {
          created = placeNewLayer(createLayer(type, t('defaults.text')), d.layers);
          return { ...d, layers: [...d.layers, created] };
        });
        if (created) setSelectedId((created as FaceLayer).id);
      },
      patch: (id, patch, coalesce) =>
        updateDoc((d) => ({ ...d, layers: d.layers.map((l) => (l.id === id ? ({ ...l, ...patch } as FaceLayer) : l)) }), coalesce ? { coalesce } : undefined),
      remove: (id) => {
        updateDoc((d) => ({ ...d, layers: d.layers.filter((l) => l.id !== id) }));
        setSelectedId((s) => (s === id ? null : s));
      },
      duplicate: (id) => {
        let copy: FaceLayer | null = null;
        updateDoc((d) => {
          const index = d.layers.findIndex((l) => l.id === id);
          if (index < 0) return d;
          copy = duplicateLayer(d.layers[index], t('layers.copySuffix'));
          const layers = [...d.layers];
          layers.splice(index + 1, 0, copy);
          return { ...d, layers };
        });
        if (copy) setSelectedId((copy as FaceLayer).id);
      },
      step: (id, direction) =>
        updateDoc((d) => {
          const index = d.layers.findIndex((l) => l.id === id);
          const target = index + direction;
          if (index < 0 || target < 0 || target >= d.layers.length) return d;
          const layers = [...d.layers];
          [layers[index], layers[target]] = [layers[target], layers[index]];
          return { ...d, layers };
        }),
      drop: (dragId, targetId, position) =>
        updateDoc((d) => {
          const moving = d.layers.find((l) => l.id === dragId);
          if (!moving || dragId === targetId) return d;
          const rest = d.layers.filter((l) => l.id !== dragId);
          const targetIndex = rest.findIndex((l) => l.id === targetId);
          if (targetIndex < 0) return d;
          // The list shows the topmost layer first: "before" in the list = above = later in the array.
          rest.splice(position === 'before' ? targetIndex + 1 : targetIndex, 0, moving);
          return { ...d, layers: rest };
        }),
    }),
    [updateDoc, t],
  );

  // ------------------------------------------------------------ keyboard

  useEffect(() => {
    const onPointerDown = (e: PointerEvent) => {
      pointerInside.current = rootRef.current?.contains(e.target as Node) ?? false;
    };
    const onKeyDown = (e: KeyboardEvent) => {
      const root = rootRef.current;
      if (!root || e.defaultPrevented || isEditable(e.target)) return;
      const active = document.activeElement;
      const inside = (active && root.contains(active)) || ((active === document.body || active === null) && pointerInside.current);
      if (!inside) return;
      const mod = e.ctrlKey || e.metaKey;
      const key = e.key.toLowerCase();
      if (mod && key === 'z') {
        e.preventDefault();
        if (e.shiftKey) history.redo();
        else history.undo();
        return;
      }
      if (mod && key === 'y') {
        e.preventDefault();
        history.redo();
        return;
      }
      if (!selected) return;
      if (e.key === 'Escape') {
        setSelectedId(null);
        return;
      }
      if (e.key === 'Delete' || e.key === 'Backspace') {
        e.preventDefault();
        ops.remove(selected.id);
        return;
      }
      if (mod && key === 'd') {
        e.preventDefault();
        ops.duplicate(selected.id);
        return;
      }
      const step = e.shiftKey ? 10 : 1;
      const delta: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
      const d = delta[e.key];
      if (!d) return;
      e.preventDefault();
      const moved =
        selected.type === 'scale'
          ? shiftScale(selected, -d[1])
          : roundPosition(offsetLayer(selected, d[0], d[1]));
      updateDoc((doc) => ({ ...doc, layers: doc.layers.map((l) => (l.id === moved.id ? moved : l)) }), { coalesce: `nudge:${selected.id}` });
    };
    window.addEventListener('pointerdown', onPointerDown, true);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('pointerdown', onPointerDown, true);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [history, selected, ops, updateDoc]);

  // ------------------------------------------------------------ labels for the layer list

  const displayName = useCallback(
    (layer: FaceLayer) => {
      const name = layer.name?.trim();
      if (!name) return t(`layerTypes.${layer.type}`);
      return GENERATED_NAMES.has(name) ? t(`generatedNames.${name}`, { defaultValue: name }) : name;
    },
    [t],
  );

  const detail = useCallback(
    (layer: FaceLayer) => {
      switch (layer.type) {
        case 'text':
          return layer.text;
        case 'icon':
          return layer.icon === '{icon}' ? t('props.pageIcon') : layer.icon;
        case 'scale':
          return t('layers.detailScale', { radius: Math.round(layer.radius) });
        case 'shape':
          return t(`shapes.${layer.kind}`);
        case 'image':
          return layer.assetId ? t('layers.detailImage') : t('layers.noImage');
        case 'bar':
        case 'sparkline':
          return layer.sensorId ? (info.sample.sensors.get(layer.sensorId)?.name ?? layer.sensorId) : t('props.pageValue');
      }
    },
    [t, info.sample.sensors],
  );

  const themeName = theme ? localized(theme.name, i18n.language) : face.theme;

  // ------------------------------------------------------------ render

  return (
    <div ref={rootRef} className={classes.root}>
      {confirmDialog}
      <div className={classes.layout}>
        <div className={classes.stageColumn}>
          <div className={classes.toolbar}>
            <Tooltip label={`${t('toolbar.undo')} (Ctrl+Z)`}>
              <ActionIcon variant="default" size="lg" onClick={history.undo} disabled={!history.canUndo} aria-label={t('toolbar.undo')}>
                <IconArrowBackUp size={18} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={`${t('toolbar.redo')} (Ctrl+Shift+Z)`}>
              <ActionIcon variant="default" size="lg" onClick={history.redo} disabled={!history.canRedo} aria-label={t('toolbar.redo')}>
                <IconArrowForwardUp size={18} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('toolbar.sweep')}>
              <ActionIcon
                variant={showSweep ? 'light' : 'default'}
                size="lg"
                onClick={() => setShowSweep(!showSweep)}
                aria-label={t('toolbar.sweep')}
                aria-pressed={showSweep}
              >
                <IconTarget size={18} />
              </ActionIcon>
            </Tooltip>
            <Group gap={6} wrap="nowrap" ml={4} style={{ minWidth: 0 }}>
              {doc ? (
                <Badge variant="light" leftSection={<IconPencil size={12} />} tt="none">
                  {t('toolbar.customLayout')}
                </Badge>
              ) : (
                <Text size="sm" c="dimmed" truncate>
                  {t('toolbar.theme')}{' '}
                  <Text span fw={600} c="var(--mantine-color-text)">
                    {themeName}
                  </Text>
                </Text>
              )}
            </Group>
            <div className={classes.toolbarSpacer} />
            {doc ? (
              <Button variant="default" leftSection={<IconRestore size={16} />} onClick={() => void backToTheme()}>
                {t('toolbar.backToTheme')}
              </Button>
            ) : (
              <Button leftSection={<IconPencil size={16} />} onClick={() => void customize()} loading={customizing}>
                {t('toolbar.customize')}
              </Button>
            )}
          </div>

          <StageArea
            slot={slot}
            pageId={page.id}
            light={page.light}
            range={{ min: info.min, max: info.max }}
            preview={preview}
            geometry={geometry}
            needle={needle}
            onNeedleChange={setNeedle}
            showSweep={showSweep}
            draggableNeedle={!doc}
            tintTarget={rootRef}
            label={t('stage.label', { title: info.sample.title })}
            overlay={
              doc
                ? (k) => (
                    <LayerCanvas
                      layers={doc.layers}
                      selectedId={selectedId}
                      onSelect={(id) => {
                        setSelectedId(id);
                        if (id) setTab('layers');
                      }}
                      onChange={replaceLayer}
                      onGestureStart={history.beginGesture}
                      onGestureEnd={history.endGesture}
                      geometry={geometry}
                      scale={k}
                      sample={info.sample}
                      label={t('stage.canvas')}
                    />
                  )
                : undefined
            }
          />
        </div>

        <div className={classes.panel}>
          <Tabs value={tab} onChange={(v) => v && setTab(v as Tab)} keepMounted={false}>
            <Tabs.List grow mb="md">
              <Tabs.Tab value="theme" leftSection={<IconPalette size={16} />}>
                {t('tabs.theme')}
              </Tabs.Tab>
              <Tabs.Tab value="options" leftSection={<IconAdjustments size={16} />}>
                {t('tabs.options')}
              </Tabs.Tab>
              <Tabs.Tab value="layers" leftSection={<IconLayersSubtract size={16} />} rightSection={doc ? <Badge size="xs" variant="light" circle>{doc.layers.length}</Badge> : undefined}>
                {t('tabs.layers')}
              </Tabs.Tab>
            </Tabs.List>

            <Tabs.Panel value="theme">
              <ThemeGallery slot={slot} page={page} face={face} themes={themesQuery.data} loading={themesQuery.isLoading} onSelect={(id) => void selectTheme(id)} />
            </Tabs.Panel>

            <Tabs.Panel value="options">
              <OptionsPanel face={face} theme={theme} update={update} defaultTitle={info.defaultTitle} defaultIcon={info.defaultIcon} />
            </Tabs.Panel>

            <Tabs.Panel value="layers">
              {doc ? (
                <LayerPanel
                  doc={doc}
                  selectedId={selectedId}
                  onSelect={setSelectedId}
                  ops={ops}
                  displayName={displayName}
                  detail={detail}
                  pageIcon={face.icon ?? info.defaultIcon}
                />
              ) : (
                <Stack align="center" gap="sm" py="xl" px="md" ta="center">
                  <ThemeIcon variant="light" size={52} radius="xl">
                    <IconLayersSubtract size={28} />
                  </ThemeIcon>
                  <Text fw={650}>{t('customize.title')}</Text>
                  <Text size="sm" c="dimmed" maw={320}>
                    {t('customize.text', { theme: themeName })}
                  </Text>
                  <Button mt="xs" leftSection={<IconPencil size={16} />} onClick={() => void customize()} loading={customizing}>
                    {t('toolbar.customize')}
                  </Button>
                </Stack>
              )}
            </Tabs.Panel>
          </Tabs>
        </div>
      </div>
    </div>
  );
}

function roundPosition(layer: FaceLayer): FaceLayer {
  if (layer.type === 'scale') return layer;
  if (layer.type === 'shape') return { ...layer, x: Math.round(layer.x), y: Math.round(layer.y), x2: Math.round(layer.x2), y2: Math.round(layer.y2) };
  return { ...layer, x: Math.round(layer.x), y: Math.round(layer.y) };
}
