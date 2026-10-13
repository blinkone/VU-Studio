import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { FaceConfig } from '@/api/types';

// The editor works on a local copy of the face so dragging and typing stay instant. Changes are pushed
// to the config draft debounced; an undo/redo stack records every edit (continuous edits coalesce).

const MAX_HISTORY = 120;
const EMIT_DELAY_MS = 250;
const COALESCE_MS = 1200;

export interface EditOptions {
  /** Edits with the same key in quick succession form one undo step (typing, nudging, sliders). */
  coalesce?: string;
}

export type FaceUpdater = (next: FaceConfig | ((face: FaceConfig) => FaceConfig), options?: EditOptions) => void;

export interface FaceHistory {
  face: FaceConfig;
  update: FaceUpdater;
  /** Starts a continuous gesture (drag): its edits become a single undo step. */
  beginGesture: () => void;
  endGesture: () => void;
  undo: () => void;
  redo: () => void;
  canUndo: boolean;
  canRedo: boolean;
}

interface State {
  face: FaceConfig;
  past: FaceConfig[];
  future: FaceConfig[];
}

function sameJson(a: unknown, b: unknown): boolean {
  return a === b || JSON.stringify(a) === JSON.stringify(b);
}

export function useFaceHistory(external: FaceConfig, onChange: (face: FaceConfig) => void): FaceHistory {
  const [state, setState] = useState<State>(() => ({ face: external, past: [], future: [] }));
  const stateRef = useRef(state);
  const lastEmitted = useRef(external);
  const pending = useRef<FaceConfig | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const onChangeRef = useRef(onChange);
  const coalesce = useRef<{ key: string | null; at: number }>({ key: null, at: 0 });
  const gesture = useRef<FaceConfig | null>(null);

  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  const flush = useCallback(() => {
    window.clearTimeout(timer.current);
    timer.current = undefined;
    const face = pending.current;
    if (face === null) return;
    pending.current = null;
    lastEmitted.current = face;
    onChangeRef.current(face);
  }, []);

  const schedule = useCallback(
    (face: FaceConfig, immediate = false) => {
      pending.current = face;
      window.clearTimeout(timer.current);
      if (immediate) flush();
      else timer.current = window.setTimeout(flush, EMIT_DELAY_MS);
    },
    [flush],
  );

  const apply = useCallback((next: State) => {
    stateRef.current = next;
    setState(next);
  }, []);

  // Flush pending edits when the editor goes away (tab switch, page switch…), so nothing is lost.
  useEffect(() => () => flush(), [flush]);

  // Adopt changes made outside the editor (discard, reload, another editor) — but not our own echo.
  useEffect(() => {
    if (external === lastEmitted.current || sameJson(external, lastEmitted.current)) {
      lastEmitted.current = external;
      return;
    }
    window.clearTimeout(timer.current);
    pending.current = null;
    lastEmitted.current = external;
    gesture.current = null;
    apply({ face: external, past: [], future: [] });
  }, [external, apply]);

  const update = useCallback<FaceUpdater>(
    (next, options) => {
      const current = stateRef.current;
      const face = typeof next === 'function' ? next(current.face) : next;
      if (face === current.face) return;
      const now = Date.now();
      const key = options?.coalesce ?? null;
      const merge = gesture.current !== null || (key !== null && coalesce.current.key === key && now - coalesce.current.at < COALESCE_MS);
      coalesce.current = { key, at: now };
      apply({
        face,
        past: merge ? current.past : [...current.past, current.face].slice(-MAX_HISTORY),
        future: [],
      });
      schedule(face, gesture.current === null && key === null);
    },
    [apply, schedule],
  );

  const beginGesture = useCallback(() => {
    gesture.current = stateRef.current.face;
    coalesce.current = { key: null, at: 0 };
  }, []);

  const endGesture = useCallback(() => {
    const before = gesture.current;
    gesture.current = null;
    const current = stateRef.current;
    if (before === null || before === current.face) return;
    apply({ ...current, past: [...current.past, before].slice(-MAX_HISTORY) });
    schedule(current.face, true);
  }, [apply, schedule]);

  const undo = useCallback(() => {
    const { face, past, future } = stateRef.current;
    if (past.length === 0) return;
    const previous = past[past.length - 1];
    coalesce.current = { key: null, at: 0 };
    apply({ face: previous, past: past.slice(0, -1), future: [face, ...future] });
    schedule(previous, true);
  }, [apply, schedule]);

  const redo = useCallback(() => {
    const { face, past, future } = stateRef.current;
    if (future.length === 0) return;
    const next = future[0];
    coalesce.current = { key: null, at: 0 };
    apply({ face: next, past: [...past, face], future: future.slice(1) });
    schedule(next, true);
  }, [apply, schedule]);

  return useMemo(
    () => ({
      face: state.face,
      update,
      beginGesture,
      endGesture,
      undo,
      redo,
      canUndo: state.past.length > 0,
      canRedo: state.future.length > 0,
    }),
    [state, update, beginGesture, endGesture, undo, redo],
  );
}
