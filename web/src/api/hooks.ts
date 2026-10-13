import { useEffect } from 'react';
import { useLive, subscribe } from './live';
import type { DialStateSnapshot, LiveValue } from './types';

/** Live value of one sensor (subscribes while mounted). */
export function useLiveValue(sensorId: string | undefined): LiveValue | undefined {
  useEffect(() => (sensorId ? subscribe([sensorId]) : undefined), [sensorId]);
  return useLive((s) => (sensorId ? s.values[sensorId] : undefined));
}

/** Subscribes to live values for a set of sensors (or '*') while mounted. */
export function useLiveSubscription(ids: string[] | '*') {
  const key = ids === '*' ? '*' : ids.join('\n');
  useEffect(() => subscribe(key === '*' ? '*' : key ? key.split('\n') : []), [key]);
}

/** Live engine state of one slot. */
export function useDialState(slot: number): DialStateSnapshot | undefined {
  return useLive((s) => s.state?.engine.dials.find((d) => d.slot === slot));
}
