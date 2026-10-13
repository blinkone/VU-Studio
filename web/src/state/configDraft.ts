import { create } from 'zustand';
import { produce, type Draft } from 'immer';
import { api, ApiError } from '@/api/client';
import type { AppConfig } from '@/api/types';

// Editing model for the whole configuration document:
//   load() → draft (mutable copy) → update(recipe) … → save() (PUT with If-Match) or reset().
// Every editor page works on the same draft, so the user can change several things and save once.

interface ConfigDraftStore {
  base: AppConfig | null;
  draft: AppConfig | null;
  dirty: boolean;
  saving: boolean;
  errors: string[];
  load: () => Promise<void>;
  /** Applies an immer recipe to the draft. */
  update: (recipe: (draft: Draft<AppConfig>) => void) => void;
  save: () => Promise<boolean>;
  reset: () => void;
}

export const useConfigDraft = create<ConfigDraftStore>((set, get) => ({
  base: null,
  draft: null,
  dirty: false,
  saving: false,
  errors: [],

  async load() {
    const config = await api.get<AppConfig>('/api/config');
    set({ base: config, draft: config, dirty: false, errors: [] });
  },

  update(recipe) {
    const { draft } = get();
    if (!draft) return;
    set({ draft: produce(draft, recipe), dirty: true });
  },

  async save() {
    const { draft } = get();
    if (!draft) return false;
    set({ saving: true, errors: [] });
    try {
      const saved = await api.put<AppConfig>('/api/config', draft, { 'If-Match': String(draft.revision) });
      set({ base: saved, draft: saved, dirty: false, saving: false });
      return true;
    } catch (error) {
      const errors =
        error instanceof ApiError
          ? error.status === 409
            ? ['conflict']
            : error.errors.length > 0
              ? error.errors
              : [error.message]
          : [String(error)];
      set({ saving: false, errors });
      return false;
    }
  },

  reset() {
    const { base } = get();
    set({ draft: base, dirty: false, errors: [] });
  },
}));

/** Reloads the config from the server when another client saved it and there are no local edits. */
export async function refreshIfClean(revision: number | null) {
  const { base, dirty, load } = useConfigDraft.getState();
  if (revision !== null && base && base.revision !== revision && !dirty) {
    await load();
  }
}

/** Short random id for new pages, profiles, layers… (matches the server's 8-hex-char style). */
export function newId(): string {
  return Math.random().toString(16).slice(2, 10).padEnd(8, '0');
}
