import { useMemo, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react';
import { useElementSize } from '@mantine/hooks';
import { formatNumber } from '@/lib/format';
import { makeTimeFormatters, timeTicks, valueScale } from './scale';
import type { SeriesPoint } from './series';
import classes from './chart.module.css';

export interface ChartSeries {
  id: string;
  name: string;
  /** Any CSS color; normally a `var(--series-N)` token from chart.module.css. */
  color: string;
  points: SeriesPoint[];
  /** Draw the min–max band behind the average line. */
  band: boolean;
}

interface TimeSeriesChartProps {
  series: ChartSeries[];
  /** X domain, epoch ms. */
  from: number;
  to: number;
  /** Canonical unit of the values (one unit per chart). */
  unit: string;
  /** Spacing of the points; a larger hole breaks the line instead of bridging it. */
  stepMs: number;
  height?: number;
  /** Sparkline-sized: fewer ticks, no end labels. */
  compact?: boolean;
  locale: string;
  /** Previous data is being refreshed: keep the frame, dim it. */
  loading?: boolean;
  ariaLabel: string;
  /** Shown over the plot while there is no data. */
  empty?: ReactNode;
  formatValue: (value: number) => string;
}

const LABEL_GAP = 15;

interface Segment {
  points: SeriesPoint[];
}

function splitSegments(points: SeriesPoint[], gap: number): Segment[] {
  const segments: Segment[] = [];
  let current: SeriesPoint[] = [];
  for (const p of points) {
    if (current.length > 0 && p.t - current[current.length - 1].t > gap) {
      segments.push({ points: current });
      current = [];
    }
    current.push(p);
  }
  if (current.length > 0) segments.push({ points: current });
  return segments;
}

function nearestIndex<T>(sorted: T[], t: number, at: (item: T) => number): number {
  let lo = 0;
  let hi = sorted.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (at(sorted[mid]) < t) lo = mid + 1;
    else hi = mid;
  }
  if (lo > 0 && Math.abs(at(sorted[lo - 1]) - t) <= Math.abs(at(sorted[lo]) - t)) return lo - 1;
  return lo;
}

const identity = (n: number) => n;
const pointTime = (p: SeriesPoint) => p.t;

function nearestPoint(points: SeriesPoint[], t: number, maxDistance: number): SeriesPoint | undefined {
  if (points.length === 0) return undefined;
  const p = points[nearestIndex(points, t, pointTime)];
  return Math.abs(p.t - t) <= maxDistance ? p : undefined;
}

/**
 * Time-series chart in plain SVG: min–max band (when the data is aggregated) under a 2 px average line,
 * hairline grid, end markers with a surface ring, and a crosshair tooltip that lists every series.
 * It follows the project's data-viz rules: one value axis, validated hues, values over labels in tooltips.
 */
export function TimeSeriesChart({ series, from, to, unit, stepMs, height = 280, compact = false, locale, loading, ariaLabel, empty, formatValue }: TimeSeriesChartProps) {
  const { ref, width } = useElementSize();
  const [hoverT, setHoverT] = useState<number | null>(null);
  const [keyboard, setKeyboard] = useState(false);
  const time = useMemo(() => makeTimeFormatters(locale), [locale]);

  const visible = useMemo(() => series.filter((s) => s.points.length > 0), [series]);
  const hasData = visible.length > 0;
  const showEndLabels = !compact && width >= 520;
  const gap = Math.max(stepMs * 2.5, 1000);

  const scale = useMemo(() => {
    let lo = Infinity;
    let hi = -Infinity;
    for (const s of visible) {
      for (const p of s.points) {
        lo = Math.min(lo, s.band ? p.min : p.avg);
        hi = Math.max(hi, s.band ? p.max : p.avg);
      }
    }
    return valueScale(lo, hi, unit, compact ? 3 : Math.max(3, Math.round(height / 52)));
  }, [visible, unit, compact, height]);

  const yLabels = useMemo(() => scale.ticks.map((tick) => formatNumber(tick, scale.decimals, locale)), [scale, locale]);
  const labelWidth = Math.max(...yLabels.map((l) => l.length)) * 6.6 + 6;
  const margin = { top: compact ? 8 : 22, right: showEndLabels ? 72 : 14, bottom: 24, left: labelWidth + 8 };
  const plotW = Math.max(10, width - margin.left - margin.right);
  const plotH = Math.max(10, height - margin.top - margin.bottom);

  const xs = (t: number) => margin.left + Math.min(1, Math.max(0, (t - from) / (to - from))) * plotW;
  const ys = (v: number) => margin.top + (1 - Math.min(1, Math.max(0, (v - scale.lo) / (scale.hi - scale.lo)))) * plotH;
  const f = (n: number) => n.toFixed(1);

  const xTicks = useMemo(() => timeTicks(from, to, Math.max(2, Math.floor(plotW / (compact ? 80 : 96)))), [from, to, plotW, compact]);

  const drawn = useMemo(
    () =>
      visible.map((s) => {
        const segments = splitSegments(s.points, gap);
        const line: string[] = [];
        const band: string[] = [];
        const lone: SeriesPoint[] = [];
        for (const { points } of segments) {
          if (points.length === 1) {
            lone.push(points[0]);
            continue;
          }
          line.push(points.map((p, i) => `${i ? 'L' : 'M'}${f(xs(p.t))},${f(ys(p.avg))}`).join(''));
          if (s.band && points.some((p) => p.max > p.min)) {
            const upper = points.map((p, i) => `${i ? 'L' : 'M'}${f(xs(p.t))},${f(ys(p.max))}`).join('');
            const lower = [...points].reverse().map((p) => `L${f(xs(p.t))},${f(ys(p.min))}`).join('');
            band.push(`${upper}${lower}Z`);
          }
        }
        return { series: s, line: line.join(''), band: band.join(''), lone };
      }),
    // xs/ys derive from these values.
    [visible, gap, from, to, plotW, plotH, scale, margin.left, margin.top],
  );

  const times = useMemo(() => {
    const all = new Set<number>();
    for (const s of visible) for (const p of s.points) all.add(p.t);
    return [...all].sort((a, b) => a - b);
  }, [visible]);

  // End labels: greedy by series order; one that would touch a placed label is left to the legend and tooltip.
  const endLabels = useMemo(() => {
    if (!showEndLabels) return [];
    const placed: number[] = [];
    const labels: { id: string; x: number; y: number; text: string }[] = [];
    for (const d of drawn) {
      const last = d.series.points[d.series.points.length - 1];
      const y = ys(last.avg);
      if (placed.some((other) => Math.abs(other - y) < LABEL_GAP)) continue;
      placed.push(y);
      labels.push({ id: d.series.id, x: xs(last.t) + 9, y, text: formatValue(last.avg) });
    }
    return labels;
  }, [drawn, showEndLabels, formatValue]);

  const moveTo = (clientX: number, element: HTMLElement) => {
    if (times.length === 0) return;
    const x = clientX - element.getBoundingClientRect().left;
    const t = from + ((x - margin.left) / plotW) * (to - from);
    setHoverT(times[nearestIndex(times, t, identity)]);
  };

  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    setKeyboard(false);
    moveTo(event.clientX, event.currentTarget);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (times.length === 0) return;
    const current = hoverT === null ? times.length : nearestIndex(times, hoverT, identity);
    let next: number | null = null;
    if (event.key === 'ArrowLeft') next = Math.max(0, current - 1);
    else if (event.key === 'ArrowRight') next = Math.min(times.length - 1, hoverT === null ? times.length - 1 : current + 1);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = times.length - 1;
    else if (event.key === 'Escape') {
      setHoverT(null);
      return;
    }
    if (next === null) return;
    event.preventDefault();
    setKeyboard(true);
    setHoverT(times[next]);
  };

  const withSeconds = stepMs < 60_000;
  const rows =
    hoverT === null
      ? []
      : visible.map((s) => ({ s, p: nearestPoint(s.points, hoverT, Math.max(stepMs, 1000)) }));
  const hoverX = hoverT === null ? 0 : xs(hoverT);
  const flip = hoverX > margin.left + plotW * 0.55;
  const summary = rows.map(({ s, p }) => `${s.name}: ${p ? formatValue(p.avg) : '–'}`).join(', ');

  return (
    <div
      ref={ref}
      className={classes.chart}
      style={{ height }}
      role="group"
      aria-label={ariaLabel}
      tabIndex={0}
      onPointerMove={onPointerMove}
      onPointerDown={onPointerMove}
      onPointerLeave={(event) => {
        if (event.pointerType !== 'touch') setHoverT(null);
      }}
      onBlur={() => setHoverT(null)}
      onKeyDown={onKeyDown}
    >
      {width > 0 && (
        <svg width={width} height={height} className={classes.svg} data-loading={loading || undefined} aria-hidden="true">
          {scale.ticks.map((tick, i) => {
            const y = ys(tick * scale.axis.factor);
            return (
              <g key={tick}>
                <line x1={margin.left} x2={margin.left + plotW} y1={y} y2={y} className={i === 0 ? classes.axis : classes.grid} />
                <text x={margin.left - 8} y={y} dy="0.32em" textAnchor="end" className={classes.tickText}>
                  {yLabels[i]}
                </text>
              </g>
            );
          })}
          {!compact && scale.axis.label && (
            <text x={margin.left - 8} y={margin.top - 11} textAnchor="end" className={classes.unitText}>
              {scale.axis.label}
            </text>
          )}
          {xTicks.ticks.map((t) => (
            <text key={t} x={xs(t)} y={margin.top + plotH + 16} textAnchor="middle" className={classes.tickText}>
              {time.tick(t, xTicks.stepMs, to - from)}
            </text>
          ))}

          {drawn.map((d) => (
            <g key={d.series.id}>
              {d.band && <path d={d.band} className={classes.band} style={{ fill: d.series.color }} />}
              {d.line && <path d={d.line} className={classes.line} style={{ stroke: d.series.color }} />}
              {d.lone.map((p) => (
                <circle key={p.t} cx={xs(p.t)} cy={ys(p.avg)} r={2.5} style={{ fill: d.series.color }} />
              ))}
              <circle
                cx={xs(d.series.points[d.series.points.length - 1].t)}
                cy={ys(d.series.points[d.series.points.length - 1].avg)}
                r={4}
                className={classes.dot}
                style={{ fill: d.series.color }}
              />
            </g>
          ))}

          {endLabels.map((label) => (
            <text key={label.id} x={label.x} y={label.y} dy="0.32em" className={classes.endText}>
              {label.text}
            </text>
          ))}

          {hoverT !== null && (
            <g>
              <line x1={hoverX} x2={hoverX} y1={margin.top} y2={margin.top + plotH} className={classes.cursor} />
              {rows.map(({ s, p }) =>
                p ? <circle key={s.id} cx={xs(p.t)} cy={ys(p.avg)} r={4.5} className={classes.dot} style={{ fill: s.color }} /> : null,
              )}
            </g>
          )}
        </svg>
      )}

      {!hasData && empty && <div className={classes.overlay}>{empty}</div>}

      {hoverT !== null && rows.length > 0 && (
        <div
          className={classes.tooltip}
          style={{ top: margin.top, left: hoverX + (flip ? -12 : 12), transform: flip ? 'translateX(-100%)' : undefined }}
        >
          <div className={classes.tooltipTime}>{time.full(hoverT, withSeconds)}</div>
          {rows.map(({ s, p }) => (
            <div key={s.id}>
              <div className={classes.tooltipRow}>
                <span className={classes.keyLine} style={{ background: s.color }} />
                <span className={classes.tooltipName}>{s.name}</span>
                <span className={classes.tooltipValue}>{p ? formatValue(p.avg) : '–'}</span>
              </div>
              {p && s.band && p.max > p.min && (
                <div className={classes.tooltipRange}>
                  {formatValue(p.min)} – {formatValue(p.max)}
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      <div className={classes.srOnly} aria-live="polite">
        {keyboard && hoverT !== null ? `${time.full(hoverT, withSeconds)}. ${summary}` : ''}
      </div>
    </div>
  );
}
