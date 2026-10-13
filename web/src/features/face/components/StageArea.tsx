import { useEffect, type CSSProperties, type ReactNode, type RefObject } from 'react';
import { ActionIcon, Box, Slider, Text, Tooltip } from '@mantine/core';
import { IconActivity, IconNeedle } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { useShallow } from 'zustand/react/shallow';
import { useLive } from '@/api/live';
import type { BacklightConfig, NeedleGeometry } from '@/api/types';
import { backlightAt, litPaper, parseRgbw } from '../model/tint';
import type { LivePreview } from '../preview/useLivePreview';
import { FaceStage } from './FaceStage';
import classes from '../FaceEditor.module.css';

export interface NeedleState {
  visible: boolean;
  /** Follow the dial's live needle (when the slot is showing this page). */
  follow: boolean;
  /** Test position 0..1 used when not following. */
  value: number;
}

interface StageAreaProps {
  slot: number;
  pageId: string;
  light: BacklightConfig;
  range: { min: number; max: number };
  preview: LivePreview;
  geometry: NeedleGeometry;
  needle: NeedleState;
  onNeedleChange: (next: NeedleState) => void;
  showSweep: boolean;
  /** Theme mode: dragging on the face moves the needle. */
  draggableNeedle: boolean;
  overlay?: (scale: number) => ReactNode;
  /** Receives the paper tint as the CSS variable --face-tint (used by the theme thumbnails). */
  tintTarget: RefObject<HTMLElement | null>;
  label: string;
}

/**
 * The stage plus the needle test control. Subscribes to the slot's live needle and backlight itself, so
 * live updates (up to 10 per second) only re-render this part of the editor.
 */
export function StageArea(props: StageAreaProps) {
  const { slot, pageId, light, range, needle, onNeedleChange, tintTarget } = props;
  const { t } = useTranslation('face');
  const live = useLive(
    useShallow((s) => {
      const dial = s.state?.engine.dials.find((d) => d.slot === slot);
      return dial && dial.pageId === pageId ? { needle: dial.needle, color: dial.previewColor } : { needle: null, color: null };
    }),
  );

  const canFollow = live.needle !== null;
  const following = needle.follow && canFollow;
  const fraction = following ? Math.min(1, Math.max(0, (live.needle ?? 0) / 100)) : needle.value;
  const rgb = following ? parseRgbw(live.color ?? undefined) : backlightAt(light, fraction, (f) => range.min + (range.max - range.min) * f);
  const tint = litPaper(rgb);

  useEffect(() => {
    tintTarget.current?.style.setProperty('--face-tint', tint);
  }, [tint, tintTarget]);

  const setValue = (value: number) => onNeedleChange({ ...needle, follow: false, value });

  return (
    <>
      <FaceStage
        preview={props.preview}
        tint={tint}
        geometry={props.geometry}
        needle={{ visible: needle.visible, fraction }}
        showSweep={props.showSweep}
        onNeedleDrag={props.draggableNeedle && needle.visible ? setValue : undefined}
        overlay={props.overlay}
        label={props.label}
      />

      <div className={classes.needleRow}>
        <Tooltip label={needle.visible ? t('needle.hide') : t('needle.show')}>
          <ActionIcon
            variant={needle.visible ? 'light' : 'subtle'}
            color={needle.visible ? undefined : 'gray'}
            aria-label={needle.visible ? t('needle.hide') : t('needle.show')}
            aria-pressed={needle.visible}
            onClick={() => onNeedleChange({ ...needle, visible: !needle.visible })}
          >
            <IconNeedle size={18} />
          </ActionIcon>
        </Tooltip>
        <Slider
          className={classes.needleSlider}
          min={0}
          max={100}
          step={1}
          size="sm"
          value={Math.round(fraction * 100)}
          onChange={(v) => setValue(v / 100)}
          disabled={!needle.visible}
          label={(v) => `${v}%`}
          aria-label={t('needle.position')}
          color={following ? 'teal' : undefined}
        />
        <Text size="xs" ff="monospace" c="dimmed" w={34} ta="right">
          {Math.round(fraction * 100)}%
        </Text>
        <Tooltip label={canFollow ? t('needle.followHint') : t('needle.followUnavailable')} multiline w={220}>
          <ActionIcon
            variant={following ? 'light' : 'subtle'}
            color={following ? 'teal' : 'gray'}
            disabled={!canFollow || !needle.visible}
            aria-label={t('needle.follow')}
            aria-pressed={following}
            onClick={() => onNeedleChange({ ...needle, follow: !needle.follow, value: fraction })}
          >
            <IconActivity size={18} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('needle.backlight')}>
          <Box className={classes.swatch} style={{ '--swatch': tint } as CSSProperties} aria-hidden />
        </Tooltip>
      </div>
    </>
  );
}
