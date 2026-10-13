import type { Profile } from '@/api/types';

export interface SourceUsage {
  /** Dial pages whose binding, face or lighting mention a sensor of the source. */
  pages: number;
  /** Profile conditions that watch one of its sensors. */
  conditions: number;
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * How much of the current configuration depends on the sensors of one provider instance.
 * Sensor ids are "{providerId}/…" and appear in JSON strings, either alone ("ping/latency")
 * or inside text templates ("{sensor:ping/latency}"), so a textual scan with a word boundary is enough.
 */
export function sourceUsage(profiles: Profile[], providerId: string): SourceUsage {
  const pattern = new RegExp(`(?:^|[^a-z0-9-])${escapeRegExp(providerId)}/`);
  const uses = (value: unknown) => pattern.test(JSON.stringify(value));

  let pages = 0;
  let conditions = 0;
  for (const profile of profiles) {
    for (const condition of profile.conditions) {
      if (uses(condition)) conditions++;
    }
    for (const assignment of profile.dials) {
      for (const page of assignment.pages) {
        if (uses(page)) pages++;
      }
    }
  }
  return { pages, conditions };
}
