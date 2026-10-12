import type { FaceLayer, NeedleGeometry } from '@/api/types';
import { estimateText, type SampleContext } from './textMetrics';

// Face-pixel geometry shared by the stage, the needle overlay and the layer editor.
// Mirrors src/VuStudio.Core/Faces/NeedleGeometry.cs: angles are degrees from vertical, clockwise positive.

export const FACE_WIDTH = 200;
export const FACE_HEIGHT = 144;
export const FACE_CENTER_X = 100;

/** Visible needle length in face pixels (from the pivot, which sits below the canvas). */
export const NEEDLE_LENGTH = 150;

export const DEFAULT_GEOMETRY: NeedleGeometry = { pivotX: 99.5, pivotY: 225.5, startAngle: -40, endAngle: 40 };

export type Point = { x: number; y: number };

export function angleAt(g: NeedleGeometry, fraction: number): number {
  return g.startAngle + (g.endAngle - g.startAngle) * fraction;
}

/** Point at `radius` from the pivot along the needle at `fraction` (0..1). */
export function pointAt(g: NeedleGeometry, fraction: number, radius: number): Point {
  return pointAtAngle(g, angleAt(g, fraction), radius);
}

export function pointAtAngle(g: NeedleGeometry, degrees: number, radius: number): Point {
  const r = (degrees * Math.PI) / 180;
  return { x: g.pivotX + radius * Math.sin(r), y: g.pivotY - radius * Math.cos(r) };
}

/** Distance of a face point from the pivot. */
export function radiusOf(g: NeedleGeometry, p: Point): number {
  return Math.hypot(p.x - g.pivotX, p.y - g.pivotY);
}

/** Dial fraction (clamped to 0..1) of the needle pointing at `p`. */
export function fractionAt(g: NeedleGeometry, p: Point): number {
  const degrees = (Math.atan2(p.x - g.pivotX, g.pivotY - p.y) * 180) / Math.PI;
  const span = g.endAngle - g.startAngle;
  if (span === 0) return 0;
  return Math.min(1, Math.max(0, (degrees - g.startAngle) / span));
}

const fmt = (n: number) => (Math.round(n * 100) / 100).toString();

/** SVG path of an annular sector between two angles (degrees from vertical). */
export function sectorPathByAngle(g: NeedleGeometry, a0: number, a1: number, inner: number, outer: number): string {
  const o0 = pointAtAngle(g, a0, outer);
  const o1 = pointAtAngle(g, a1, outer);
  const large = Math.abs(a1 - a0) > 180 ? 1 : 0;
  const sweep = a1 > a0 ? 1 : 0;
  if (inner <= 0.25) {
    return `M${fmt(g.pivotX)} ${fmt(g.pivotY)}L${fmt(o0.x)} ${fmt(o0.y)}A${fmt(outer)} ${fmt(outer)} 0 ${large} ${sweep} ${fmt(o1.x)} ${fmt(o1.y)}Z`;
  }
  const i1 = pointAtAngle(g, a1, inner);
  const i0 = pointAtAngle(g, a0, inner);
  return (
    `M${fmt(o0.x)} ${fmt(o0.y)}A${fmt(outer)} ${fmt(outer)} 0 ${large} ${sweep} ${fmt(o1.x)} ${fmt(o1.y)}` +
    `L${fmt(i1.x)} ${fmt(i1.y)}A${fmt(inner)} ${fmt(inner)} 0 ${large} ${1 - sweep} ${fmt(i0.x)} ${fmt(i0.y)}Z`
  );
}

/** SVG path of an annular sector between two dial fractions. */
export function sectorPath(g: NeedleGeometry, from: number, to: number, inner: number, outer: number): string {
  return sectorPathByAngle(g, angleAt(g, from), angleAt(g, to), inner, outer);
}

/** SVG path of a circular arc (no fill) between two dial fractions. */
export function arcPath(g: NeedleGeometry, from: number, to: number, radius: number): string {
  const p0 = pointAt(g, from, radius);
  const p1 = pointAt(g, to, radius);
  const a0 = angleAt(g, from);
  const a1 = angleAt(g, to);
  return `M${fmt(p0.x)} ${fmt(p0.y)}A${fmt(radius)} ${fmt(radius)} 0 ${Math.abs(a1 - a0) > 180 ? 1 : 0} ${a1 > a0 ? 1 : 0} ${fmt(p1.x)} ${fmt(p1.y)}`;
}

/** The area the needle can sweep (pivot → full sweep at the needle length). */
export function sweepPath(g: NeedleGeometry, length = NEEDLE_LENGTH): string {
  return sectorPath(g, 0, 1, 0, length);
}

// ---------------------------------------------------------------- layer outlines

export type LayerOutline =
  /** Axis-aligned box, optionally rotated around (cx, cy). */
  | { kind: 'box'; x: number; y: number; w: number; h: number; rotation?: number; cx?: number; cy?: number }
  | { kind: 'line'; x1: number; y1: number; x2: number; y2: number; width: number }
  /** Annular sector around the pivot (scale layers). */
  | { kind: 'band'; inner: number; outer: number; a0: number; a1: number };

function box(x: number, y: number, w: number, h: number): LayerOutline {
  return { kind: 'box', x: Math.min(x, x + w), y: Math.min(y, y + h), w: Math.abs(w), h: Math.abs(h) };
}

/** Radii covered by a scale layer: ticks, labels, arc and zones. */
export function scaleExtent(layer: Extract<FaceLayer, { type: 'scale' }>): { inner: number; outer: number } {
  const radii: number[] = [];
  const tick = Math.max(layer.majorLength, layer.minorLength, 1);
  radii.push(layer.radius, layer.radius - tick);
  if (layer.showLabels) {
    radii.push(layer.labelRadius - layer.labelSize * 0.6, layer.labelRadius + layer.labelSize * 0.6);
  }
  if (layer.showArc) {
    const r = layer.arcRadius ?? layer.radius;
    radii.push(r - layer.arcWidth / 2, r + layer.arcWidth / 2);
  }
  for (const z of layer.zones) radii.push(z.innerRadius, z.outerRadius);
  const finite = radii.filter((r) => Number.isFinite(r));
  return { inner: Math.max(0, Math.min(...finite)), outer: Math.max(...finite) };
}

/** Approximate on-canvas outline of a layer, used for hit testing and selection. */
export function layerOutline(layer: FaceLayer, g: NeedleGeometry, sample: SampleContext): LayerOutline {
  switch (layer.type) {
    case 'text': {
      const m = estimateText(layer, sample);
      const left = layer.align === 'left' ? layer.x : layer.align === 'right' ? layer.x - m.width : layer.x - m.width / 2;
      const baseline =
        layer.baseline === 'top'
          ? layer.y + m.cap
          : layer.baseline === 'middle'
            ? layer.y + m.cap / 2
            : layer.baseline === 'bottom'
              ? layer.y - m.descent
              : layer.y;
      const top = baseline - m.cap - 1;
      const bottom = baseline + m.descent * 0.55 + 1;
      return {
        kind: 'box',
        x: left - 1,
        y: top,
        w: Math.max(4, m.width + 2),
        h: Math.max(4, bottom - top),
        rotation: layer.rotation || undefined,
        cx: layer.x,
        cy: layer.y,
      };
    }
    case 'icon':
      return box(layer.x - layer.size / 2, layer.y - layer.size / 2, layer.size, layer.size);
    case 'image':
    case 'bar':
    case 'sparkline':
      return box(layer.x, layer.y, layer.width, layer.height);
    case 'shape':
      if (layer.kind === 'line') {
        return { kind: 'line', x1: layer.x, y1: layer.y, x2: layer.x2, y2: layer.y2, width: layer.stroke ? layer.strokeWidth : 1 };
      }
      return box(layer.x, layer.y, layer.width, layer.height);
    case 'scale': {
      const { inner, outer } = scaleExtent(layer);
      // Labels stick out a little beyond the end ticks.
      const margin = layer.showLabels ? 3 : 1;
      return { kind: 'band', inner, outer, a0: g.startAngle - margin, a1: g.endAngle + margin };
    }
  }
}

/** Axis-aligned bounds of an outline (for guides and keeping things on screen). */
export function outlineBounds(o: LayerOutline, g: NeedleGeometry): { x: number; y: number; w: number; h: number } {
  switch (o.kind) {
    case 'box': {
      if (!o.rotation) return { x: o.x, y: o.y, w: o.w, h: o.h };
      const r = (o.rotation * Math.PI) / 180;
      const cos = Math.cos(r);
      const sin = Math.sin(r);
      const cx = o.cx ?? o.x;
      const cy = o.cy ?? o.y;
      const pts = [
        [o.x, o.y],
        [o.x + o.w, o.y],
        [o.x, o.y + o.h],
        [o.x + o.w, o.y + o.h],
      ].map(([x, y]) => [cx + (x - cx) * cos - (y - cy) * sin, cy + (x - cx) * sin + (y - cy) * cos]);
      const xs = pts.map((p) => p[0]);
      const ys = pts.map((p) => p[1]);
      return { x: Math.min(...xs), y: Math.min(...ys), w: Math.max(...xs) - Math.min(...xs), h: Math.max(...ys) - Math.min(...ys) };
    }
    case 'line':
      return { x: Math.min(o.x1, o.x2), y: Math.min(o.y1, o.y2), w: Math.abs(o.x2 - o.x1), h: Math.abs(o.y2 - o.y1) };
    case 'band': {
      const top = g.pivotY - o.outer;
      const left = pointAtAngle(g, o.a0, o.outer).x;
      const right = pointAtAngle(g, o.a1, o.outer).x;
      const bottom = Math.max(pointAtAngle(g, o.a0, o.inner).y, pointAtAngle(g, o.a1, o.inner).y);
      return { x: left, y: top, w: right - left, h: bottom - top };
    }
  }
}

export const clampFace = (v: number, min = -400, max = 600) => Math.min(max, Math.max(min, v));
