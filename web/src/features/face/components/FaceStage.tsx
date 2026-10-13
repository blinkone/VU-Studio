import { useRef, type CSSProperties, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { Loader, Text } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { useTranslation } from 'react-i18next';
import type { NeedleGeometry } from '@/api/types';
import { FACE_HEIGHT, FACE_WIDTH, fractionAt, NEEDLE_LENGTH, pointAt, sectorPath, sweepPath } from '../model/geometry';
import type { LivePreview } from '../preview/useLivePreview';
import classes from '../FaceEditor.module.css';

interface FaceStageProps {
  preview: LivePreview;
  /** Paper color (the backlight shining through the e-paper). */
  tint: string;
  geometry: NeedleGeometry;
  needle: { visible: boolean; fraction: number };
  showSweep: boolean;
  /** When set, dragging on the face moves the test needle. */
  onNeedleDrag?: (fraction: number) => void;
  /** Editing overlay, drawn above the face in face-pixel coordinates; receives the display scale. */
  overlay?: (scale: number) => ReactNode;
  label: string;
}

/** Display scale for an available width: whole multiples (crisp pixels) when there is room. */
function displayScale(available: number): number {
  const unit = FACE_WIDTH + 9; // face + bezel
  if (available <= 0) return 3;
  if (available >= unit * 4) return 4;
  if (available >= unit * 3) return 3;
  if (available >= unit * 2) return 2;
  return Math.max(1, available / unit);
}

/** The dial as it will look: a bezel, the 1-bit preview lit by the backlight, and the needle. */
export function FaceStage({ preview, tint, geometry, needle, showSweep, onNeedleDrag, overlay, label }: FaceStageProps) {
  const { t } = useTranslation('face');
  const { ref, width } = useElementSize();
  const k = displayScale(width);
  const dragging = useRef<number | null>(null);

  const toFraction = (event: ReactPointerEvent<HTMLDivElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    const x = ((event.clientX - rect.left) / rect.width) * FACE_WIDTH;
    const y = ((event.clientY - rect.top) / rect.height) * FACE_HEIGHT;
    return fractionAt(geometry, { x, y });
  };

  const dragHandlers = onNeedleDrag
    ? {
        onPointerDown: (event: ReactPointerEvent<HTMLDivElement>) => {
          if (event.button !== 0) return;
          dragging.current = event.pointerId;
          event.currentTarget.setPointerCapture(event.pointerId);
          onNeedleDrag(toFraction(event));
        },
        onPointerMove: (event: ReactPointerEvent<HTMLDivElement>) => {
          if (dragging.current === event.pointerId) onNeedleDrag(toFraction(event));
        },
        onPointerUp: (event: ReactPointerEvent<HTMLDivElement>) => {
          if (dragging.current === event.pointerId) dragging.current = null;
        },
      }
    : {};

  const tip = pointAt(geometry, needle.fraction, NEEDLE_LENGTH);
  const angle = geometry.startAngle + (geometry.endAngle - geometry.startAngle) * needle.fraction;
  const style = { '--tint': tint } as CSSProperties;

  return (
    <div className={classes.stage} style={style}>
      <div className={classes.stageInner} ref={ref}>
        <div className={classes.bezel} style={{ '--k': k } as CSSProperties}>
          <div
            className={`${classes.face} ${onNeedleDrag ? classes.needleDraggable : ''}`}
            role="img"
            aria-label={label}
            {...dragHandlers}
          >
            {preview.url && preview.status !== 'unavailable' && (
              <img
                src={preview.url}
                alt=""
                draggable={false}
                className={`${classes.faceImage} ${preview.busy ? classes.faceImageBusy : ''}`}
              />
            )}

            {preview.status === 'unavailable' && <UnavailableFace geometry={geometry} message={t('stage.unavailable')} hint={t('stage.unavailableHint')} />}

            <svg className={classes.faceOverlay} viewBox={`0 0 ${FACE_WIDTH} ${FACE_HEIGHT}`} style={{ pointerEvents: 'none' }} aria-hidden>
              {showSweep && (
                <path
                  d={sweepPath(geometry)}
                  fill="rgba(255, 150, 20, 0.09)"
                  stroke="rgba(230, 120, 0, 0.75)"
                  strokeWidth={1}
                  strokeDasharray="4 3"
                  vectorEffect="non-scaling-stroke"
                />
              )}
              {needle.visible && (
                <g>
                  <line
                    x1={geometry.pivotX + 1.2}
                    y1={geometry.pivotY + 1.2}
                    x2={tip.x + 1.2}
                    y2={tip.y + 1.2}
                    stroke="rgba(0, 0, 0, 0.28)"
                    strokeWidth={2.4}
                    strokeLinecap="round"
                  />
                  <polygon
                    points={needlePolygon(geometry.pivotX, geometry.pivotY, angle)}
                    fill="#d9381e"
                    stroke="rgba(90, 10, 0, 0.55)"
                    strokeWidth={0.35}
                  />
                </g>
              )}
            </svg>

            {overlay?.(k)}

            {(preview.status === 'loading' || (preview.busy && !preview.url)) && (
              <Loader size="xs" className={classes.loaderDot} color="dark" type="dots" />
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

/** A tapered needle from the pivot (below the canvas) to its tip. */
function needlePolygon(px: number, py: number, angle: number): string {
  const r = (angle * Math.PI) / 180;
  const ux = Math.sin(r);
  const uy = -Math.cos(r);
  const nx = -uy;
  const ny = ux;
  const base = 2.6;
  const tipWidth = 0.55;
  const L = NEEDLE_LENGTH;
  const pts = [
    [px + nx * base, py + ny * base],
    [px + ux * L + nx * tipWidth, py + uy * L + ny * tipWidth],
    [px + ux * (L + 1.2), py + uy * (L + 1.2)],
    [px + ux * L - nx * tipWidth, py + uy * L - ny * tipWidth],
    [px - nx * base, py - ny * base],
  ];
  return pts.map(([x, y]) => `${x.toFixed(2)},${y.toFixed(2)}`).join(' ');
}

function UnavailableFace({ geometry, message, hint }: { geometry: NeedleGeometry; message: string; hint: string }) {
  const ticks = Array.from({ length: 11 }, (_, i) => i / 10);
  return (
    <div className={classes.unavailable}>
      <svg className={classes.dialGhost} viewBox={`0 0 ${FACE_WIDTH} ${FACE_HEIGHT}`} aria-hidden>
        <path d={sectorPath(geometry, 0, 1, 140, 142)} fill="rgba(0,0,0,0.28)" />
        {ticks.map((f) => {
          const a = pointAt(geometry, f, f % 0.5 === 0 ? 128 : 134);
          const b = pointAt(geometry, f, 141);
          return <line key={f} x1={a.x} y1={a.y} x2={b.x} y2={b.y} stroke="rgba(0,0,0,0.28)" strokeWidth={f % 0.5 === 0 ? 1.6 : 1} />;
        })}
      </svg>
      <Text size="sm" fw={650} style={{ position: 'relative' }}>
        {message}
      </Text>
      <Text size="xs" style={{ position: 'relative', maxWidth: '80%' }}>
        {hint}
      </Text>
    </div>
  );
}
