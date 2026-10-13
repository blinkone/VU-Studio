// The service stores times of day as .NET TimeOnly, serialized as "HH:mm:ss".

/** "HH:mm:ss" or "HH:mm" → "HH:mm" (what <input type="time"> understands). */
export function toHm(value: string | undefined, fallback = '00:00'): string {
  const match = /^(\d{1,2}):(\d{2})/.exec(value ?? '');
  return match ? `${match[1].padStart(2, '0')}:${match[2]}` : fallback;
}

/** "HH:mm" → "HH:mm:ss", the format the service expects. */
export function toTimeOnly(hm: string): string {
  return `${toHm(hm)}:00`;
}

/** True when the interval runs past midnight (the end is earlier than the start). */
export function wrapsPastMidnight(from: string, to: string): boolean {
  return toHm(from) > toHm(to);
}
