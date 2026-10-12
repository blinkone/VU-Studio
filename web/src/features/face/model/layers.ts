import type { ComponentType } from 'react';
import {
  IconChartLine,
  IconGauge,
  IconIcons,
  IconLetterT,
  IconPhoto,
  IconChartBar,
  IconShape,
  type IconProps,
} from '@tabler/icons-react';
import type {
  BarLayer,
  FaceDocument,
  FaceLayer,
  IconLayer,
  ImageLayer,
  ScaleLayer,
  ScaleZone,
  ShapeLayer,
  SparklineLayer,
  TextLayer,
} from '@/api/types';
import { newId } from '@/state/configDraft';

export type LayerType = FaceLayer['type'];
export type LayerOf<T extends LayerType> = Extract<FaceLayer, { type: T }>;

/** Order of the "Add layer" menu. */
export const LAYER_TYPES: LayerType[] = ['text', 'icon', 'scale', 'shape', 'bar', 'sparkline', 'image'];

export const LAYER_ICONS: Record<LayerType, ComponentType<IconProps>> = {
  text: IconLetterT,
  icon: IconIcons,
  scale: IconGauge,
  shape: IconShape,
  bar: IconChartBar,
  sparkline: IconChartLine,
  image: IconPhoto,
};

const base = () => ({ id: newId(), name: undefined, visible: true, ink: 'foreground' as const });

// The service's JSON reader needs the "type" discriminator to come first, so it leads every literal.
// New layers start from the service's defaults (src/VuStudio.Core/Faces/FaceDocument.cs). Every property
// is listed, including the nullable ones (undefined = null on the service), so the editor always works
// on complete objects.

export function defaultText(text: string): TextLayer {
  return {
    type: 'text',
    ...base(),
    x: 100,
    y: 30,
    text,
    font: 'inter',
    size: 18,
    weight: 600,
    italic: false,
    align: 'center',
    baseline: 'middle',
    maxWidth: undefined,
    overflow: 'shrink',
    letterSpacing: 0,
    uppercase: false,
    smooth: true,
    rotation: 0,
  };
}

export function defaultIcon(): IconLayer {
  return { type: 'icon', ...base(), x: 100, y: 30, size: 24, icon: '{icon}' };
}

export function defaultZone(radius = 152): ScaleZone {
  return { from: 0.85, to: 1, style: 'band', innerRadius: Math.max(0, radius - 14), outerRadius: radius };
}

export function defaultScale(): ScaleLayer {
  return {
    type: 'scale',
    ...base(),
    radius: 152,
    majorLength: 14,
    minorLength: 6,
    majorWidth: 2.5,
    minorWidth: 1.2,
    tickStyle: 'line',
    majorCount: 5,
    minorPerMajor: 4,
    evenSpacing: false,
    showLabels: true,
    labelRadius: 124,
    labelFont: 'barlow-condensed',
    labelSize: 14,
    labelWeight: 600,
    labelFormat: undefined,
    labelDivisor: 1,
    customLabels: undefined,
    rotateLabels: false,
    showArc: false,
    arcRadius: undefined,
    arcWidth: 1.5,
    zones: [],
  };
}

export function defaultImage(): ImageLayer {
  return {
    type: 'image',
    ...base(),
    assetId: '',
    x: 0,
    y: 0,
    width: 200,
    height: 144,
    fit: 'contain',
    dither: 'floydSteinberg',
    threshold: 128,
    brightness: 0,
    contrast: 0,
    invertImage: false,
  };
}

export function defaultShape(): ShapeLayer {
  // Centred in the top band, which the needle never crosses.
  return {
    type: 'shape',
    ...base(),
    kind: 'rectangle',
    x: 80,
    y: 20,
    width: 40,
    height: 20,
    x2: 120,
    y2: 20,
    strokeWidth: 1.5,
    fill: false,
    stroke: true,
    cornerRadius: 0,
    startAngle: 0,
    sweepAngle: 90,
    dashed: false,
  };
}

export function defaultBar(): BarLayer {
  return {
    type: 'bar',
    ...base(),
    x: 20,
    y: 120,
    width: 160,
    height: 8,
    sensorId: undefined,
    min: undefined,
    max: undefined,
    orientation: 'horizontal',
    segments: 0,
    gap: 1,
    border: true,
  };
}

export function defaultSparkline(): SparklineLayer {
  return {
    type: 'sparkline',
    ...base(),
    x: 20,
    y: 96,
    width: 160,
    height: 24,
    sensorId: undefined,
    windowSeconds: 300,
    style: 'line',
    strokeWidth: 1.5,
    min: undefined,
    max: undefined,
    border: false,
  };
}

export function createLayer(type: LayerType, defaultTextContent: string): FaceLayer {
  switch (type) {
    case 'text':
      return defaultText(defaultTextContent);
    case 'icon':
      return defaultIcon();
    case 'scale':
      return defaultScale();
    case 'image':
      return defaultImage();
    case 'shape':
      return defaultShape();
    case 'bar':
      return defaultBar();
    case 'sparkline':
      return defaultSparkline();
  }
}

function position(layer: FaceLayer): [number, number] | null {
  return 'x' in layer ? [layer.x, layer.y] : null;
}

/** Shifts a new layer so it does not sit exactly on top of an existing layer of the same type. */
export function placeNewLayer(layer: FaceLayer, existing: FaceLayer[]): FaceLayer {
  if (!('x' in layer)) return layer;
  let next: FaceLayer = layer;
  for (let i = 0; i < 12; i++) {
    const p = position(next);
    const clash = existing.some((l) => {
      const q = position(l);
      return l.type === next.type && q !== null && p !== null && Math.abs(q[0] - p[0]) < 1 && Math.abs(q[1] - p[1]) < 1;
    });
    if (!clash) break;
    next = offsetLayer(next, 6, 6);
  }
  return next;
}

export function offsetLayer(layer: FaceLayer, dx: number, dy: number): FaceLayer {
  switch (layer.type) {
    case 'scale':
      return layer;
    case 'shape':
      return { ...layer, x: layer.x + dx, y: layer.y + dy, x2: layer.x2 + dx, y2: layer.y2 + dy };
    default:
      return { ...layer, x: layer.x + dx, y: layer.y + dy };
  }
}

/** A copy with a new id, nudged so it is visibly separate from the original. */
export function duplicateLayer(layer: FaceLayer, copySuffix: string): FaceLayer {
  const clone = structuredClone(layer) as FaceLayer;
  clone.id = newId();
  if (layer.name) clone.name = `${layer.name} ${copySuffix}`;
  return offsetLayer(clone, 4, 4);
}

export function emptyDocument(inverted: boolean): FaceDocument {
  return { version: 1, inverted, layers: [] };
}

/** Guarantees every layer has a unique id (documents from older versions or hand-edited JSON may not). */
export function normalizeDocument(doc: FaceDocument): FaceDocument {
  const seen = new Set<string>();
  let changed = false;
  const layers = (doc.layers ?? []).map((layer) => {
    if (!layer.id || seen.has(layer.id)) {
      changed = true;
      const fixed = { ...layer, id: newId() } as FaceLayer;
      seen.add(fixed.id);
      return fixed;
    }
    seen.add(layer.id);
    return layer;
  });
  return changed || !doc.layers ? { ...doc, layers } : doc;
}
