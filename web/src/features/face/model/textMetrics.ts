import type { SensorDto, TextLayer } from '@/api/types';
import { formatValue } from '@/lib/format';

// Approximate text measurement for the layer editor's selection outlines. The face fonts live in the
// service (SkiaSharp), not in the browser, so widths are estimated from per-font advance factors.

/** What placeholders resolve to while estimating text bounds (live values where available). */
export interface SampleContext {
  title: string;
  subtitle: string;
  value: string;
  unit: string;
  min: string;
  max: string;
  percent: string;
  sensors: Map<string, SensorDto>;
  locale?: string;
}

export const EMPTY_SAMPLE: SampleContext = {
  title: 'Title',
  subtitle: '',
  value: '88',
  unit: '%',
  min: '0',
  max: '100',
  percent: '88',
  sensors: new Map(),
};

/** Resolves a text template roughly like the service's TextTemplate/FaceRenderContext. */
export function resolveSample(template: string, s: SampleContext): string {
  let out = '';
  for (let i = 0; i < template.length; i++) {
    const c = template[i];
    if ((c === '{' || c === '}') && template[i + 1] === c) {
      out += c;
      i++;
      continue;
    }
    if (c !== '{') {
      out += c;
      continue;
    }
    const end = template.indexOf('}', i + 1);
    if (end < 0) {
      out += template.slice(i);
      break;
    }
    out += resolveToken(template.slice(i + 1, end), s);
    i = end;
  }
  return out;
}

function resolveToken(body: string, s: SampleContext): string {
  const pipe = body.lastIndexOf('|');
  const head = pipe >= 0 ? body.slice(0, pipe) : body;
  const colon = head.indexOf(':');
  const name = (colon >= 0 ? head.slice(0, colon) : head).trim().toLowerCase();
  const arg = colon >= 0 ? head.slice(colon + 1) : undefined;
  switch (name) {
    case 'title':
      return s.title;
    case 'subtitle':
      return s.subtitle;
    case 'unit':
      return s.unit;
    case 'value':
      return s.value;
    case 'min':
      return s.min;
    case 'max':
      return s.max;
    case 'percent':
      return s.percent;
    case 'icon':
      return '';
    case 'time':
    case 'date':
      // Every format letter becomes a digit-wide character: "HH:mm" → "88:88".
      return (arg || (name === 'time' ? 'HH:mm' : 'dd/MM/yyyy')).replace(/[A-Za-z]/g, '8');
    case 'sensor':
    case 'sensor.value':
    case 'sensor.unit':
    case 'sensor.name': {
      const sensor = arg ? s.sensors.get(arg.trim()) : undefined;
      if (!sensor) return name === 'sensor.unit' ? '' : '–';
      if (name === 'sensor.name') return sensor.name;
      if (name === 'sensor.unit') return sensor.unit;
      if (sensor.valueKind !== 'number') return sensor.text ?? '–';
      const text = formatValue(sensor.value, sensor.unit, s.locale);
      return name === 'sensor.value' ? text.replace(/\s*[^\d.,\-−]+$/, '') : text;
    }
    default:
      return `{${body}}`;
  }
}

interface FontMetrics {
  /** Width relative to Inter for proportional fonts. */
  scale: number;
  /** Fixed advance in em for monospaced fonts. */
  mono?: number;
  /** Cap height in em. */
  cap: number;
}

const FONTS: Record<string, FontMetrics> = {
  inter: { scale: 1, cap: 0.727 },
  'barlow-condensed': { scale: 0.78, cap: 0.7 },
  b612: { scale: 1.08, cap: 0.72 },
  'b612-mono': { scale: 1, mono: 0.6, cap: 0.72 },
  orbitron: { scale: 1.3, cap: 0.72 },
  'share-tech-mono': { scale: 1, mono: 0.52, cap: 0.7 },
  'libre-baskerville': { scale: 1.08, cap: 0.7 },
  oswald: { scale: 0.8, cap: 0.81 },
  'bebas-neue': { scale: 0.72, cap: 0.7 },
  silkscreen: { scale: 1.3, cap: 0.63 },
  vt323: { scale: 1, mono: 0.4, cap: 0.58 },
  'jetbrains-mono': { scale: 1, mono: 0.6, cap: 0.73 },
  'space-grotesk': { scale: 1.02, cap: 0.7 },
  teko: { scale: 0.7, cap: 0.62 },
};

/** Inter-like advance widths in em per character class. */
function charWidth(c: string): number {
  if (c === ' ') return 0.27;
  if ('il.,:;\'|!'.includes(c)) return 0.26;
  if ('1'.includes(c)) return 0.48;
  if ('fjrt()[]-'.includes(c)) return 0.36;
  if ('mwMW@%'.includes(c)) return 0.86;
  if (/[0-9]/.test(c)) return 0.6;
  if (/[A-Z]/.test(c)) return 0.68;
  if ('°'.includes(c)) return 0.38;
  return 0.54;
}

export interface TextEstimate {
  width: number;
  cap: number;
  descent: number;
  text: string;
}

/** Estimated rendered size of a text layer, honouring max width and overflow. */
export function estimateText(layer: TextLayer, sample: SampleContext): TextEstimate {
  let text = resolveSample(layer.text, sample);
  if (layer.uppercase) text = text.toUpperCase();
  const m = FONTS[layer.font] ?? FONTS.inter;
  const size = Math.max(1, layer.size || 1);
  const weightFactor = m.mono ? 1 : 1 + ((Math.min(900, Math.max(100, layer.weight)) - 400) / 1000) * 0.28;
  let em = 0;
  for (const c of text) em += m.mono ?? charWidth(c) * m.scale;
  let width = em * size * weightFactor + Math.max(0, text.length - 1) * (layer.letterSpacing || 0);
  let cap = m.cap * size;
  let descent = 0.24 * size;
  if (layer.maxWidth !== undefined && layer.maxWidth > 0 && width > layer.maxWidth && layer.overflow !== 'visible') {
    if (layer.overflow === 'shrink') {
      const ratio = Math.max(6 / size, layer.maxWidth / width);
      cap *= ratio;
      descent *= ratio;
    }
    width = layer.maxWidth;
  }
  return { width, cap, descent, text };
}
