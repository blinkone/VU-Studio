import { useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode, type SVGProps } from 'react';
import type { FaceLayer, NeedleGeometry, ScaleLayer } from '@/api/types';
import {
  FACE_CENTER_X,
  FACE_HEIGHT,
  FACE_WIDTH,
  layerOutline,
  outlineBounds,
  pointAtAngle,
  radiusOf,
  sectorPathByAngle,
  type LayerOutline,
  type Point,
} from '../model/geometry';
import type { SampleContext } from '../model/textMetrics';
import classes from '../FaceEditor.module.css';

const ACCENT = '#ffb11f';
const HALO = 'rgba(20, 16, 6, 0.55)';

type BoxHandle = 'nw' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w';
type Handle = BoxHandle | 'p1' | 'p2' | 'radius' | 'labelRadius';

interface Drag {
  pointerId: number;
  handle: Handle | 'move';
  start: Point;
  orig: FaceLayer;
  started: boolean;
}

interface LayerCanvasProps {
  layers: FaceLayer[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  onChange: (layer: FaceLayer) => void;
  onGestureStart: () => void;
  onGestureEnd: () => void;
  geometry: NeedleGeometry;
  /** Display scale (screen pixels per face pixel). */
  scale: number;
  sample: SampleContext;
  label: string;
}

const CURSORS: Record<BoxHandle, string> = {
  nw: 'nwse-resize',
  se: 'nwse-resize',
  ne: 'nesw-resize',
  sw: 'nesw-resize',
  n: 'ns-resize',
  s: 'ns-resize',
  e: 'ew-resize',
  w: 'ew-resize',
};

const r = Math.round;

function isBoxLayer(layer: FaceLayer): layer is Extract<FaceLayer, { width: number }> {
  return (layer.type === 'image' || layer.type === 'bar' || layer.type === 'sparkline' || layer.type === 'shape') && !(layer.type === 'shape' && layer.kind === 'line');
}

/** Moves a layer by (dx, dy) face pixels, snapping the result to whole pixels. */
function moveLayer(orig: FaceLayer, dx: number, dy: number, radial: number): FaceLayer {
  switch (orig.type) {
    case 'scale':
      return shiftScale(orig, radial);
    case 'shape':
      return { ...orig, x: r(orig.x + dx), y: r(orig.y + dy), x2: r(orig.x2 + dx), y2: r(orig.y2 + dy) };
    default:
      return { ...orig, x: r(orig.x + dx), y: r(orig.y + dy) };
  }
}

/** Moves a whole scale in or out: every radius changes by the same amount. */
export function shiftScale(orig: ScaleLayer, delta: number): ScaleLayer {
  const d = r(delta);
  if (d === 0) return orig;
  return {
    ...orig,
    radius: orig.radius + d,
    labelRadius: orig.labelRadius + d,
    arcRadius: orig.arcRadius === undefined ? undefined : orig.arcRadius + d,
    zones: orig.zones.map((z) => ({ ...z, innerRadius: z.innerRadius + d, outerRadius: z.outerRadius + d })),
  };
}

function resizeBox(orig: Extract<FaceLayer, { width: number }>, handle: BoxHandle, p: Point, keepRatio: boolean): FaceLayer {
  let left = orig.x;
  let top = orig.y;
  let right = orig.x + orig.width;
  let bottom = orig.y + orig.height;
  if (handle.includes('w')) left = Math.min(r(p.x), right - 1);
  if (handle.includes('e')) right = Math.max(r(p.x), left + 1);
  if (handle.includes('n')) top = Math.min(r(p.y), bottom - 1);
  if (handle.includes('s')) bottom = Math.max(r(p.y), top + 1);
  if (keepRatio && handle.length === 2 && orig.width > 0 && orig.height > 0) {
    const ratio = orig.height / orig.width;
    const h = Math.max(1, r((right - left) * ratio));
    if (handle.includes('n')) top = bottom - h;
    else bottom = top + h;
  }
  return { ...orig, x: left, y: top, width: right - left, height: bottom - top } as FaceLayer;
}

function applyHandle(drag: Drag, p: Point, g: NeedleGeometry, keepRatio: boolean): FaceLayer {
  const orig = drag.orig;
  switch (drag.handle) {
    case 'move':
      return moveLayer(orig, p.x - drag.start.x, p.y - drag.start.y, radiusOf(g, p) - radiusOf(g, drag.start));
    case 'p1':
      return orig.type === 'shape' ? { ...orig, x: r(p.x), y: r(p.y) } : orig;
    case 'p2':
      return orig.type === 'shape' ? { ...orig, x2: r(p.x), y2: r(p.y) } : orig;
    case 'radius':
      return orig.type === 'scale' ? { ...orig, radius: Math.max(1, r(radiusOf(g, p))) } : orig;
    case 'labelRadius':
      return orig.type === 'scale' ? { ...orig, labelRadius: Math.max(1, r(radiusOf(g, p))) } : orig;
    default:
      if (orig.type === 'icon') {
        const half = Math.max(Math.abs(p.x - orig.x), Math.abs(p.y - orig.y));
        return { ...orig, size: Math.max(4, r(half * 2)) };
      }
      return isBoxLayer(orig) ? resizeBox(orig, drag.handle, p, keepRatio) : orig;
  }
}

/** Horizontal centre of a layer (for snapping to the face centre), or null when it does not apply. */
function centerX(layer: FaceLayer, outline: LayerOutline, g: NeedleGeometry): number | null {
  if (layer.type === 'scale') return null;
  if (layer.type === 'text') return layer.align === 'center' ? layer.x : null;
  if (layer.type === 'icon') return layer.x;
  const b = outlineBounds(outline, g);
  return b.x + b.w / 2;
}

function OutlineShape({ outline, g, ...props }: { outline: LayerOutline; g: NeedleGeometry } & SVGProps<SVGElement>): ReactNode {
  const common = props as SVGProps<SVGRectElement & SVGPathElement & SVGLineElement>;
  switch (outline.kind) {
    case 'box':
      return (
        <rect
          x={outline.x}
          y={outline.y}
          width={outline.w}
          height={outline.h}
          transform={outline.rotation ? `rotate(${outline.rotation} ${outline.cx} ${outline.cy})` : undefined}
          {...common}
        />
      );
    case 'line':
      return <line x1={outline.x1} y1={outline.y1} x2={outline.x2} y2={outline.y2} {...common} />;
    case 'band':
      return <path d={sectorPathByAngle(g, outline.a0, outline.a1, outline.inner, outline.outer)} {...common} />;
  }
}

/**
 * SVG overlay in face-pixel coordinates: hover and selection outlines, drag to move, resize handles,
 * line end points and scale radii. Bounds of text are estimates (fonts live in the service).
 */
export function LayerCanvas({ layers, selectedId, onSelect, onChange, onGestureStart, onGestureEnd, geometry: g, scale, sample, label }: LayerCanvasProps) {
  const svg = useRef<SVGSVGElement>(null);
  const drag = useRef<Drag | null>(null);
  const [hoverId, setHoverId] = useState<string | null>(null);
  const [snapGuide, setSnapGuide] = useState(false);
  const [activeHandle, setActiveHandle] = useState<Handle | 'move' | null>(null);

  const outlines = useMemo(() => layers.map((layer) => ({ layer, outline: layerOutline(layer, g, sample) })), [layers, g, sample]);
  const selected = outlines.find((o) => o.layer.id === selectedId) ?? null;
  const hovered = hoverId !== selectedId ? (outlines.find((o) => o.layer.id === hoverId) ?? null) : null;
  const unit = 1 / scale; // one screen pixel in face pixels
  const handleSize = 7 * unit;

  const toFace = (event: { clientX: number; clientY: number }): Point => {
    const el = svg.current;
    const ctm = el?.getScreenCTM();
    if (!el || !ctm) return { x: 0, y: 0 };
    const pt = new DOMPoint(event.clientX, event.clientY).matrixTransform(ctm.inverse());
    return { x: pt.x, y: pt.y };
  };

  const begin = (event: ReactPointerEvent, layer: FaceLayer, handle: Handle | 'move') => {
    if (event.button !== 0) return;
    event.stopPropagation();
    event.preventDefault();
    onSelect(layer.id);
    (event.currentTarget.closest('[tabindex]') as HTMLElement | null)?.focus({ preventScroll: true });
    svg.current?.setPointerCapture(event.pointerId);
    drag.current = { pointerId: event.pointerId, handle, start: toFace(event), orig: layer, started: false };
    setActiveHandle(handle);
  };

  const onPointerMove = (event: ReactPointerEvent<SVGSVGElement>) => {
    const d = drag.current;
    if (!d || d.pointerId !== event.pointerId) return;
    const p = toFace(event);
    if (!d.started) {
      if (Math.hypot(p.x - d.start.x, p.y - d.start.y) * scale < 3) return;
      d.started = true;
      onGestureStart();
    }
    let next = applyHandle(d, p, g, event.shiftKey);
    let snapped = false;
    if (d.handle === 'move' && !event.altKey) {
      const cx = centerX(next, layerOutline(next, g, sample), g);
      if (cx !== null && Math.abs(cx - FACE_CENTER_X) <= 2.5 && Math.abs(cx - FACE_CENTER_X) > 0) {
        const shift = FACE_CENTER_X - cx;
        next = moveLayer(next, shift, 0, 0);
        snapped = true;
      } else if (cx !== null && Math.abs(cx - FACE_CENTER_X) < 0.01) {
        snapped = true;
      }
    }
    setSnapGuide(snapped);
    onChange(next);
  };

  const end = (event: ReactPointerEvent<SVGSVGElement>) => {
    const d = drag.current;
    if (!d || d.pointerId !== event.pointerId) return;
    drag.current = null;
    setSnapGuide(false);
    setActiveHandle(null);
    if (d.started) onGestureEnd();
  };

  const handles: { id: Handle; at: Point; cursor: string }[] = [];
  if (selected && selected.layer.visible) {
    const { layer, outline } = selected;
    if (layer.type === 'shape' && layer.kind === 'line') {
      handles.push({ id: 'p1', at: { x: layer.x, y: layer.y }, cursor: 'crosshair' }, { id: 'p2', at: { x: layer.x2, y: layer.y2 }, cursor: 'crosshair' });
    } else if (layer.type === 'scale') {
      handles.push({ id: 'radius', at: pointAtAngle(g, -24, layer.radius), cursor: 'ns-resize' });
      if (layer.showLabels) handles.push({ id: 'labelRadius', at: pointAtAngle(g, 24, layer.labelRadius), cursor: 'ns-resize' });
    } else if (outline.kind === 'box' && (isBoxLayer(layer) || layer.type === 'icon')) {
      const { x, y, w, h } = outline;
      const all: [BoxHandle, number, number][] = [
        ['nw', x, y],
        ['n', x + w / 2, y],
        ['ne', x + w, y],
        ['e', x + w, y + h / 2],
        ['se', x + w, y + h],
        ['s', x + w / 2, y + h],
        ['sw', x, y + h],
        ['w', x, y + h / 2],
      ];
      for (const [id, hx, hy] of all) {
        if (layer.type === 'icon' && id.length === 1) continue;
        handles.push({ id, at: { x: hx, y: hy }, cursor: CURSORS[id] });
      }
    }
  }

  const scaleLayer = selected?.layer.type === 'scale' ? selected.layer : null;

  return (
    <svg
      ref={svg}
      className={classes.faceOverlay}
      viewBox={`0 0 ${FACE_WIDTH} ${FACE_HEIGHT}`}
      onPointerDown={(e) => {
        if (e.button === 0 && e.target === svg.current) onSelect(null);
      }}
      onPointerMove={onPointerMove}
      onPointerUp={end}
      onPointerCancel={end}
      onPointerLeave={() => setHoverId(null)}
      role="presentation"
      aria-label={label}
    >
      {/* Hit areas, in drawing order so the topmost layer wins. */}
      {outlines.map(({ layer, outline }) =>
        layer.visible ? (
          <OutlineShape
            key={layer.id}
            outline={outline}
            g={g}
            fill={outline.kind === 'line' ? 'none' : 'transparent'}
            stroke="transparent"
            strokeWidth={outline.kind === 'line' ? Math.max(outline.width, 8 * unit) : 0}
            style={{ cursor: 'move', pointerEvents: outline.kind === 'line' ? 'stroke' : 'all' }}
            onPointerDown={(e: ReactPointerEvent) => begin(e, layer, 'move')}
            onPointerEnter={() => setHoverId(layer.id)}
            onPointerLeave={() => setHoverId((h) => (h === layer.id ? null : h))}
          />
        ) : null,
      )}

      {hovered && !drag.current && (
        <OutlineShape outline={hovered.outline} g={g} fill="none" stroke={ACCENT} strokeWidth={1} strokeDasharray="3 2" vectorEffect="non-scaling-stroke" style={{ pointerEvents: 'none' }} />
      )}

      {snapGuide && <line x1={FACE_CENTER_X} y1={0} x2={FACE_CENTER_X} y2={FACE_HEIGHT} stroke="#ff4fa3" strokeWidth={1} vectorEffect="non-scaling-stroke" style={{ pointerEvents: 'none' }} />}

      {selected && (
        <g style={{ pointerEvents: 'none' }}>
          <OutlineShape outline={selected.outline} g={g} fill="none" stroke={HALO} strokeWidth={3} vectorEffect="non-scaling-stroke" />
          <OutlineShape
            outline={selected.outline}
            g={g}
            fill={selected.layer.type === 'scale' ? 'rgba(255, 177, 31, 0.07)' : 'none'}
            stroke={ACCENT}
            strokeWidth={1.5}
            strokeDasharray={selected.layer.visible ? undefined : '4 3'}
            vectorEffect="non-scaling-stroke"
          />
          {selected.layer.type === 'text' && (
            <g stroke={ACCENT} strokeWidth={1.25} vectorEffect="non-scaling-stroke">
              <line x1={selected.layer.x - 3 * unit * 1.5} y1={selected.layer.y} x2={selected.layer.x + 3 * unit * 1.5} y2={selected.layer.y} vectorEffect="non-scaling-stroke" />
              <line x1={selected.layer.x} y1={selected.layer.y - 3 * unit * 1.5} x2={selected.layer.x} y2={selected.layer.y + 3 * unit * 1.5} vectorEffect="non-scaling-stroke" />
            </g>
          )}
          {scaleLayer && activeHandle && activeHandle !== 'move' && (
            <circle
              cx={g.pivotX}
              cy={g.pivotY}
              r={activeHandle === 'labelRadius' ? scaleLayer.labelRadius : scaleLayer.radius}
              fill="none"
              stroke={ACCENT}
              strokeWidth={1}
              strokeDasharray="2 3"
              vectorEffect="non-scaling-stroke"
            />
          )}
        </g>
      )}

      {handles.map((h) => (
        <rect
          key={h.id}
          x={h.at.x - handleSize / 2}
          y={h.at.y - handleSize / 2}
          width={handleSize}
          height={handleSize}
          rx={h.id === 'radius' || h.id === 'labelRadius' || h.id === 'p1' || h.id === 'p2' ? handleSize / 2 : unit}
          fill="#fff"
          stroke="#c27800"
          strokeWidth={1.25}
          vectorEffect="non-scaling-stroke"
          style={{ cursor: h.cursor }}
          onPointerDown={(e) => selected && begin(e, selected.layer, h.id)}
        />
      ))}
    </svg>
  );
}
