// Provider instance ids become the prefix of every sensor id ("{id}/{localPath}"), so the service is
// strict about them (see ProviderManager.ValidId and ConfigService.Validate).

export const PROVIDER_ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,62}$/;

/** Ids the service keeps for itself ("session" is the tray app's sensor namespace). */
const RESERVED_IDS: ReadonlySet<string> = new Set(['session']);

export type IdProblem = 'empty' | 'pattern' | 'reserved' | 'duplicate';

/** Turns a provider type into a valid id: lowercase letters, digits and hyphens only. */
export function slugify(value: string): string {
  const slug = value
    .toLowerCase()
    .replace(/[^a-z0-9-]+/g, '-')
    .replace(/-{2,}/g, '-')
    .replace(/^-+/, '')
    .slice(0, 56)
    .replace(/-+$/, '');
  return slug || 'source';
}

/** Light clean-up while the user types an id: lowercase, spaces and underscores become hyphens. */
export function normalizeIdInput(value: string): string {
  return value.toLowerCase().replace(/[\s_]+/g, '-');
}

/** `type`, then `type-2`, `type-3`… — the first id that is neither taken nor reserved. */
export function suggestId(type: string, taken: ReadonlySet<string>): string {
  const base = slugify(type);
  let candidate = base;
  for (let n = 2; taken.has(candidate) || RESERVED_IDS.has(candidate); n++) {
    const suffix = `-${n}`;
    candidate = `${base.slice(0, 63 - suffix.length)}${suffix}`;
  }
  return candidate;
}

/** Returns what is wrong with an id, or null when it can be used. */
export function checkId(id: string, taken: ReadonlySet<string>): IdProblem | null {
  if (id === '') return 'empty';
  if (!PROVIDER_ID_PATTERN.test(id)) return 'pattern';
  if (RESERVED_IDS.has(id)) return 'reserved';
  if (taken.has(id)) return 'duplicate';
  return null;
}
