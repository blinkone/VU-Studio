// Number/unit formatting that mirrors the service's UnitConverter (canonical units → readable text).

const byteLadder = ['B', 'KB', 'MB', 'GB', 'TB'];
const rateLadder = ['B/s', 'KB/s', 'MB/s', 'GB/s'];

export function formatNumber(value: number, decimals?: number, locale?: string): string {
  if (!Number.isFinite(value)) return '–';
  const abs = Math.abs(value);
  const d = decimals ?? (abs >= 100 ? 0 : abs >= 10 ? 1 : abs === 0 ? 0 : 2);
  return value.toLocaleString(locale, { minimumFractionDigits: d, maximumFractionDigits: d });
}

/** Formats a value in its canonical unit, scaling bytes, rates and MHz automatically. */
export function formatValue(value: number | null | undefined, unit: string, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '–';
  let v = value;
  let u = unit;
  const ladder = unit === 'B' ? byteLadder : unit === 'B/s' ? rateLadder : null;
  if (ladder) {
    let i = 0;
    while (Math.abs(v) >= 1024 && i < ladder.length - 1) {
      v /= 1024;
      i++;
    }
    u = ladder[i];
  } else if (unit === 'MHz' && Math.abs(v) >= 1000) {
    v /= 1000;
    u = 'GHz';
  }
  const number = formatNumber(v, undefined, locale);
  if (!u) return number;
  return u === '%' || u.startsWith('°') ? `${number}${u}` : `${number} ${u}`;
}
