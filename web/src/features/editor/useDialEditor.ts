import { useCallback, useEffect, useMemo } from 'react';
import { useSearchParams } from 'react-router';
import type { Draft } from 'immer';
import { useLive } from '@/api/live';
import type { AppConfig, DialAssignment, DialPage, Profile } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';

export type EditorTab = 'data' | 'face' | 'light' | 'pages';
const TABS: EditorTab[] = ['data', 'face', 'light', 'pages'];

/** The profile that always matches and whose assignments other profiles inherit (DialEngine.Tick). */
export function findDefaultProfile(profiles: Profile[]): Profile | undefined {
  return profiles.find((p) => p.enabled && p.activation === 'always') ?? profiles[0];
}

const findAssignment = (profile: Profile | Draft<Profile> | undefined, slot: number) => profile?.dials.find((d) => d.slot === slot);

export interface DialEditorModel {
  slot: number;
  config: AppConfig;
  /** The saved configuration (for unsaved-change detection). */
  base: AppConfig | null;
  profile: Profile;
  defaultProfile: Profile | undefined;
  isDefaultProfile: boolean;
  /** True when the chosen profile is the one the engine is running now. */
  isActiveProfile: boolean;
  activeProfileName: string | undefined;
  assignment: DialAssignment | undefined;
  page: DialPage | undefined;
  pageIndex: number;
  tab: EditorTab;
  setProfile: (id: string) => void;
  setPage: (id: string) => void;
  setTab: (tab: EditorTab) => void;
  /** Applies a recipe to this slot's assignment in the chosen profile. */
  updateAssignment: (recipe: (assignment: Draft<DialAssignment>) => void) => void;
  /** Applies a recipe to the selected page. */
  updatePage: (recipe: (page: Draft<DialPage>) => void) => void;
  /** Applies a recipe to the chosen profile (e.g. to add or remove the assignment). */
  updateProfile: (recipe: (profile: Draft<Profile>) => void) => void;
}

/**
 * Resolves which profile/assignment/page the dial editor works on. The choice lives in the URL
 * (`?profile=…&page=…&tab=…`) and is pinned on first load, so automatic profile switches or page
 * rotation never move the editor under the user's hands.
 */
export function useDialEditor(slot: number): DialEditorModel | null {
  const config = useConfigDraft((s) => s.draft);
  const base = useConfigDraft((s) => s.base);
  const update = useConfigDraft((s) => s.update);
  const live = useLive((s) => s.state);
  const liveStatus = useLive((s) => s.status);
  const liveDial = live?.engine.dials.find((d) => d.slot === slot);
  const [params, setParams] = useSearchParams();

  const profiles = config?.profiles;
  const defaultProfile = useMemo(() => (profiles ? findDefaultProfile(profiles) : undefined), [profiles]);
  const activeProfileId = live?.engine.activeProfileId;

  const requestedProfile = params.get('profile');
  const profile = useMemo(() => {
    if (!profiles) return undefined;
    const requested = requestedProfile ? profiles.find((p) => p.id === requestedProfile) : undefined;
    if (requested) return requested;
    const active = profiles.find((p) => p.id === activeProfileId);
    if (active && (findAssignment(active, slot)?.pages.length ?? 0) > 0) return active;
    return defaultProfile;
  }, [profiles, requestedProfile, activeProfileId, slot, defaultProfile]);

  const assignment = findAssignment(profile, slot);
  const requestedPage = params.get('page');
  const pageIndex = useMemo(() => {
    const pages = assignment?.pages ?? [];
    if (pages.length === 0) return -1;
    const byParam = requestedPage ? pages.findIndex((p) => p.id === requestedPage) : -1;
    if (byParam >= 0) return byParam;
    if (liveDial?.profileId === profile?.id && liveDial?.pageId) {
      const byLive = pages.findIndex((p) => p.id === liveDial.pageId);
      if (byLive >= 0) return byLive;
    }
    return 0;
  }, [assignment, requestedPage, liveDial?.profileId, liveDial?.pageId, profile?.id]);
  const page = pageIndex >= 0 ? assignment?.pages[pageIndex] : undefined;

  const tabParam = params.get('tab') as EditorTab | null;
  const tab: EditorTab = tabParam && TABS.includes(tabParam) ? tabParam : 'data';

  // Pin the resolved profile and page once the live state is known.
  const liveReady = live !== null || liveStatus === 'closed';
  useEffect(() => {
    if (!profile || !liveReady) return;
    const needProfile = params.get('profile') !== profile.id;
    const needPage = page !== undefined && params.get('page') !== page.id;
    if (!needProfile && !needPage) return;
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set('profile', profile.id);
        if (page) next.set('page', page.id);
        return next;
      },
      { replace: true },
    );
  }, [profile, page, liveReady, params, setParams]);

  const setParam = useCallback(
    (key: string, value: string | null) =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          if (value === null) next.delete(key);
          else next.set(key, value);
          if (key === 'profile') next.delete('page');
          return next;
        },
        { replace: true },
      ),
    [setParams],
  );

  const profileId = profile?.id;
  const pageId = page?.id;

  const updateProfile = useCallback(
    (recipe: (profile: Draft<Profile>) => void) =>
      update((draft) => {
        const p = draft.profiles.find((x) => x.id === profileId);
        if (p) recipe(p);
      }),
    [update, profileId],
  );

  const updateAssignment = useCallback(
    (recipe: (assignment: Draft<DialAssignment>) => void) =>
      update((draft) => {
        const a = findAssignment(
          draft.profiles.find((x) => x.id === profileId),
          slot,
        );
        if (a) recipe(a);
      }),
    [update, profileId, slot],
  );

  const updatePage = useCallback(
    (recipe: (page: Draft<DialPage>) => void) =>
      update((draft) => {
        const p = findAssignment(
          draft.profiles.find((x) => x.id === profileId),
          slot,
        )?.pages.find((x) => x.id === pageId);
        if (p) recipe(p);
      }),
    [update, profileId, slot, pageId],
  );

  if (!config || !profile) return null;

  return {
    slot,
    config,
    base,
    profile,
    defaultProfile,
    isDefaultProfile: profile.id === defaultProfile?.id,
    isActiveProfile: profile.id === activeProfileId,
    activeProfileName: live?.engine.activeProfileName,
    assignment,
    page,
    pageIndex,
    tab,
    setProfile: (id) => setParam('profile', id),
    setPage: (id) => setParam('page', id),
    setTab: (t) => setParam('tab', t === 'data' ? null : t),
    updateAssignment,
    updatePage,
    updateProfile,
  };
}
