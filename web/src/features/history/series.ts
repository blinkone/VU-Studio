import { api } from '@/api/client';
import type { HistoryAggregateDto, HistoryPointDto } from '@/api/types';

// ---------------------------------------------------------------- ranges

export const RANGES = ['15m', '1h', '6h', '24h', '7d', '30d'] as const;
export type RangeKey = (typeof RANGES)[number];

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

export const RANGE_MS: Record<RangeKey, number> = {
  '15m': 15 * MINUTE,
  '1h': HOUR,
  '6h': 6 * HOUR,
  '24h': DAY,
  '7d': 7 * DAY,
  '30d': 30 * DAY,
};

/** How often a range is reloaded while the page is open. */
export const REFRESH_MS: Record<RangeKey, number> = {
  '15m': 2_000,
  '1h': 20_000,
  '6h': 30_000,
  '24h': 60_000,
  '7d': 120_000,
  '30d': 300_000,
};

export function parseRange(value: string | null): RangeKey {
  return (RANGES as readonly string[]).includes(value ?? '') ? (value as RangeKey) : '1h';
}

// ---------------------------------------------------------------- data

/** One plotted point. Raw samples have min = avg = max and n = 1. */
export interface SeriesPoint {
  t: number;
  min: number;
  avg: number;
  max: number;
  /** Number of raw samples behind the point (weights the average when merging). */
  n: number;
}

export interface SeriesData {
  id: string;
  points: SeriesPoint[];
  /** Spacing of the underlying data: 1 s for the recent view, 1 min for aggregates. */
  resolutionMs: number;
  /** Client time of the request: the right edge of the chart. */
  fetchedAt: number;
}

const finite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value);

export async function fetchSeries(id: string, range: RangeKey): Promise<SeriesData> {
  const sensor = encodeURIComponent(id);
  const now = Date.now();

  if (range === '15m') {
    const rows = await api.get<HistoryPointDto[]>(`/api/history/recent?sensor=${sensor}&seconds=${RANGE_MS[range] / 1000}`);
    return { id, fetchedAt: now, resolutionMs: 1000, points: toRawPoints(rows) };
  }

  const from = new Date(now - RANGE_MS[range]).toISOString();
  const to = new Date(now).toISOString();
  const rows = await api.get<HistoryAggregateDto[]>(`/api/history?sensor=${sensor}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
  return { id, fetchedAt: now, resolutionMs: MINUTE, points: toAggregatePoints(rows) };
}

/** Recent samples for the sensor drawer. */
export async function fetchRecent(id: string, seconds: number): Promise<SeriesData> {
  const rows = await api.get<HistoryPointDto[]>(`/api/history/recent?sensor=${encodeURIComponent(id)}&seconds=${seconds}`);
  return { id, fetchedAt: Date.now(), resolutionMs: 1000, points: toRawPoints(rows) };
}

export function toRawPoints(rows: HistoryPointDto[]): SeriesPoint[] {
  const points: SeriesPoint[] = [];
  for (const row of rows ?? []) {
    const t = Date.parse(row.time);
    const value = Number(row.value);
    if (Number.isFinite(t) && finite(value)) points.push({ t, min: value, avg: value, max: value, n: 1 });
  }
  return points.sort((a, b) => a.t - b.t);
}

export function toAggregatePoints(rows: HistoryAggregateDto[]): SeriesPoint[] {
  const points: SeriesPoint[] = [];
  for (const row of rows ?? []) {
    const t = Date.parse(row.minute);
    const min = Number(row.min);
    const avg = Number(row.avg);
    const max = Number(row.max);
    if (Number.isFinite(t) && finite(min) && finite(avg) && finite(max)) {
      points.push({ t, min, avg, max, n: Math.max(1, Number(row.count) || 1) });
    }
  }
  return points.sort((a, b) => a.t - b.t);
}

// ---------------------------------------------------------------- downsampling

const NICE_BUCKETS_MS = [
  ...[2, 3, 5, 10, 15, 30].map((s) => s * 1000),
  ...[1, 2, 3, 5, 10, 15, 20, 30, 60, 120, 180, 240, 360, 720, 1440].map((m) => m * MINUTE),
];

/**
 * Merges points into buckets so a long range draws a few hundred points. Min and max keep the true
 * extremes, the average is weighted by the number of samples. Short ranges stay untouched.
 */
export function downsample(points: SeriesPoint[], spanMs: number, targetCount: number, resolutionMs: number): { points: SeriesPoint[]; stepMs: number } {
  if (points.length <= targetCount) return { points, stepMs: resolutionMs };

  const wanted = spanMs / targetCount;
  const bucket = NICE_BUCKETS_MS.find((size) => size >= wanted && size > resolutionMs) ?? Math.ceil(wanted / MINUTE) * MINUTE;

  const out: SeriesPoint[] = [];
  let current: { key: number; min: number; max: number; sum: number; n: number } | null = null;
  const flush = () => {
    if (current) out.push({ t: current.key * bucket, min: current.min, avg: current.sum / current.n, max: current.max, n: current.n });
  };
  for (const p of points) {
    const key = Math.floor(p.t / bucket);
    if (!current || current.key !== key) {
      flush();
      current = { key, min: p.min, max: p.max, sum: p.avg * p.n, n: p.n };
    } else {
      current.min = Math.min(current.min, p.min);
      current.max = Math.max(current.max, p.max);
      current.sum += p.avg * p.n;
      current.n += p.n;
    }
  }
  flush();
  return { points: out, stepMs: bucket };
}

// ---------------------------------------------------------------- statistics

export interface SeriesStats {
  min: number;
  avg: number;
  max: number;
  last: number;
  firstT: number;
  lastT: number;
}

export function computeStats(points: SeriesPoint[]): SeriesStats | null {
  if (points.length === 0) return null;
  let min = Infinity;
  let max = -Infinity;
  let sum = 0;
  let n = 0;
  for (const p of points) {
    min = Math.min(min, p.min);
    max = Math.max(max, p.max);
    sum += p.avg * p.n;
    n += p.n;
  }
  const last = points[points.length - 1];
  return { min, max, avg: sum / n, last: last.avg, firstT: points[0].t, lastT: last.t };
}

// ---------------------------------------------------------------- CSV

export interface CsvSeries {
  id: string;
  name: string;
  unit: string;
  points: SeriesPoint[];
}

export interface CsvOptions {
  /** "excel" writes what a European spreadsheet opens directly: semicolons, decimal commas, local time. */
  flavor: 'standard' | 'excel';
}

const pad = (n: number) => String(n).padStart(2, '0');

function localTimestamp(t: number): string {
  const d = new Date(t);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

export function buildCsv(series: CsvSeries[], { flavor }: CsvOptions): string {
  const delimiter = flavor === 'excel' ? ';' : ',';
  const number = (value: number) => {
    const text = String(Number(value.toFixed(6)));
    return flavor === 'excel' ? text.replace('.', ',') : text;
  };
  const text = (value: string) => {
    // Names come from providers: neutralise spreadsheet formulas, then quote when needed.
    const safe = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;
    return /[",;\n\r]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe;
  };

  const lines = [['sensor_id', 'sensor_name', 'unit', 'time', 'min', 'avg', 'max', 'samples'].join(delimiter)];
  for (const s of series) {
    for (const p of s.points) {
      lines.push(
        [
          text(s.id),
          text(s.name),
          text(s.unit),
          flavor === 'excel' ? localTimestamp(p.t) : new Date(p.t).toISOString(),
          number(p.min),
          number(p.avg),
          number(p.max),
          String(p.n),
        ].join(delimiter),
      );
    }
  }
  return `${lines.join('\r\n')}\r\n`;
}

export function downloadCsv(filename: string, csv: string, flavor: CsvOptions['flavor']) {
  // Excel needs the BOM to read UTF-8 (degree signs, accents).
  const blob = new Blob([flavor === 'excel' ? '﻿' : '', csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
