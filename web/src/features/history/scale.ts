// Axis maths for the charts: round tick values, unit scaling (bytes → MB…), and time ticks on local boundaries.

const SECOND = 1000;
const MINUTE = 60 * SECOND;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

// ---------------------------------------------------------------- value axis

export interface AxisUnit {
  /** Divide canonical values by this to get the numbers printed on the axis. */
  factor: number;
  /** Unit shown once above the axis. */
  label: string;
}

const BYTE_LADDER = ['B', 'KB', 'MB', 'GB', 'TB'];
const RATE_LADDER = ['B/s', 'KB/s', 'MB/s', 'GB/s'];

/** Picks one display unit for the whole axis, so tick labels are plain numbers. */
export function axisUnit(unit: string, maxAbs: number): AxisUnit {
  const ladder = unit === 'B' ? BYTE_LADDER : unit === 'B/s' ? RATE_LADDER : null;
  if (ladder) {
    let step = 0;
    while (step < ladder.length - 1 && maxAbs >= 1024 ** (step + 1)) step++;
    return { factor: 1024 ** step, label: ladder[step] };
  }
  if (unit === 'MHz' && maxAbs >= 1000) return { factor: 1000, label: 'GHz' };
  return { factor: 1, label: unit };
}

function niceStep(range: number, count: number): number {
  const raw = range / Math.max(1, count);
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const normalized = raw / magnitude;
  const step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
  return step * magnitude;
}

export interface ValueScale {
  /** Domain in canonical units (ticks included). */
  lo: number;
  hi: number;
  ticks: number[];
  axis: AxisUnit;
  /** Decimals that tick labels need. */
  decimals: number;
}

/**
 * Value axis for a set of data extremes. Percentages get the familiar 0–100 scale; other loads start at
 * zero when the data does; temperatures and the like zoom in on what actually moves.
 */
export function valueScale(dataLo: number, dataHi: number, unit: string, targetTicks: number): ValueScale {
  let lo = dataLo;
  let hi = dataHi;
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) {
    lo = 0;
    hi = 1;
  }

  if (unit === '%' && lo >= 0 && hi <= 100) {
    lo = 0;
    hi = 100;
  } else if (lo >= 0 && lo <= hi * 0.35) {
    lo = 0;
  }
  if (hi - lo < 1e-9) {
    const pad = Math.abs(hi) > 1e-9 ? Math.abs(hi) * 0.1 : 1;
    lo -= pad;
    hi += pad;
  }

  const axis = axisUnit(unit, Math.max(Math.abs(lo), Math.abs(hi)));
  const dLo = lo / axis.factor;
  const dHi = hi / axis.factor;

  const percentFull = unit === '%' && dLo === 0 && dHi === 100;
  const step = percentFull ? 25 : niceStep(dHi - dLo, targetTicks);
  const first = Math.floor(dLo / step + 1e-9) * step;
  const last = Math.ceil(dHi / step - 1e-9) * step;
  const decimals = Math.max(0, -Math.floor(Math.log10(step) + 1e-9));

  const ticks: number[] = [];
  for (let v = first; v <= last + step * 1e-6; v += step) ticks.push(Number(v.toFixed(decimals + 2)));

  return { lo: first * axis.factor, hi: last * axis.factor, ticks, axis, decimals };
}

// ---------------------------------------------------------------- time axis

const TIME_STEPS = [
  1, 2, 5, 10, 15, 30, // seconds
  60, 120, 300, 600, 900, 1800, // minutes
  3600, 7200, 10800, 21600, 43200, // hours
  86400, 172800, 432000, 604800, 1209600, // days
].map((s) => s * SECOND);

export interface TimeTicks {
  ticks: number[];
  stepMs: number;
}

/** Tick positions on round local times (full hours, local midnights…) for the span from→to. */
export function timeTicks(from: number, to: number, targetCount: number): TimeTicks {
  const stepMs = TIME_STEPS.find((s) => (to - from) / s <= targetCount) ?? TIME_STEPS[TIME_STEPS.length - 1];
  const ticks: number[] = [];

  if (stepMs < HOUR) {
    for (let t = Math.ceil(from / stepMs) * stepMs; t <= to; t += stepMs) ticks.push(t);
  } else if (stepMs < DAY) {
    const hours = stepMs / HOUR;
    const day = new Date(from);
    day.setHours(0, 0, 0, 0);
    for (; day.getTime() <= to; day.setDate(day.getDate() + 1)) {
      for (let h = 0; h < 24; h += hours) {
        const t = new Date(day.getFullYear(), day.getMonth(), day.getDate(), h).getTime();
        if (t >= from && t <= to) ticks.push(t);
      }
    }
  } else {
    const days = stepMs / DAY;
    const day = new Date(from);
    day.setHours(0, 0, 0, 0);
    if (day.getTime() < from) day.setDate(day.getDate() + 1);
    for (; day.getTime() <= to; day.setDate(day.getDate() + days)) ticks.push(day.getTime());
  }
  return { ticks, stepMs };
}

export interface TimeFormatters {
  tick: (t: number, stepMs: number, spanMs: number) => string;
  /** Full date and time for tooltips. */
  full: (t: number, withSeconds: boolean) => string;
  time: (t: number, withSeconds?: boolean) => string;
  dateTime: (t: number) => string;
}

/** Locale-aware formatters; time is always 24-hour, like the rest of a hardware monitor. */
export function makeTimeFormatters(locale: string): TimeFormatters {
  const make = (options: Intl.DateTimeFormatOptions) => {
    try {
      return new Intl.DateTimeFormat(locale, { hourCycle: 'h23', ...options });
    } catch {
      return new Intl.DateTimeFormat(undefined, { hourCycle: 'h23', ...options });
    }
  };
  const hm = make({ hour: '2-digit', minute: '2-digit' });
  const hms = make({ hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const dayMonth = make({ day: 'numeric', month: 'short' });
  const weekdayDay = make({ weekday: 'short', day: 'numeric' });
  const full = make({ weekday: 'short', day: 'numeric', month: 'short' });
  const dateTime = make({ day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

  const isMidnight = (t: number) => {
    const d = new Date(t);
    return d.getHours() === 0 && d.getMinutes() === 0 && d.getSeconds() === 0;
  };

  return {
    tick: (t, stepMs, spanMs) => {
      if (stepMs >= DAY) return (spanMs <= 8 * DAY ? weekdayDay : dayMonth).format(t);
      if (isMidnight(t) && spanMs > 20 * HOUR) return dayMonth.format(t);
      return (stepMs < MINUTE ? hms : hm).format(t);
    },
    full: (t, withSeconds) => `${full.format(t)}, ${(withSeconds ? hms : hm).format(t)}`,
    time: (t, withSeconds) => (withSeconds ? hms : hm).format(t),
    dateTime: (t) => dateTime.format(t),
  };
}
