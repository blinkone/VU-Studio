import { create } from 'zustand';
import type { QueryClient } from '@tanstack/react-query';
import { queryKeys } from './queries';
import type { LiveValue, NotificationDto, StateDto } from './types';

type ConnectionStatus = 'connecting' | 'open' | 'closed';

interface LiveStore {
  status: ConnectionStatus;
  state: StateDto | null;
  /** Latest live values by sensor id (only for subscribed sensors). */
  values: Record<string, LiveValue>;
  lastNotification: NotificationDto | null;
  configRevision: number | null;
}

export const useLive = create<LiveStore>(() => ({
  status: 'connecting',
  state: null,
  values: {},
  lastNotification: null,
  configRevision: null,
}));

let socket: WebSocket | null = null;
let retry = 0;
const subscriptions = new Map<string, number>();
let subscribeAll = 0;

function sendSubscription() {
  if (socket?.readyState !== WebSocket.OPEN) return;
  const sensors = subscribeAll > 0 ? '*' : [...subscriptions.keys()];
  socket.send(JSON.stringify({ type: 'subscribe', sensors }));
}

/**
 * Subscribes to live values for the given sensor ids ('*' for all). Returns an unsubscribe function.
 * Use the useLiveValue/useLiveSubscription hooks rather than calling this directly from components.
 */
export function subscribe(ids: string[] | '*'): () => void {
  if (ids === '*') {
    subscribeAll++;
  } else {
    for (const id of ids) subscriptions.set(id, (subscriptions.get(id) ?? 0) + 1);
  }
  sendSubscription();
  return () => {
    if (ids === '*') {
      subscribeAll = Math.max(0, subscribeAll - 1);
    } else {
      for (const id of ids) {
        const n = (subscriptions.get(id) ?? 1) - 1;
        if (n <= 0) subscriptions.delete(id);
        else subscriptions.set(id, n);
      }
    }
    sendSubscription();
  };
}

/** Opens the /api/live WebSocket (once) and keeps it connected. */
export function connectLive(queryClient: QueryClient) {
  if (socket) return;

  const open = () => {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${protocol}//${location.host}/api/live`);
    socket = ws;
    useLive.setState({ status: 'connecting' });

    ws.onopen = () => {
      retry = 0;
      useLive.setState({ status: 'open' });
      sendSubscription();
    };

    ws.onmessage = (event) => {
      const message = JSON.parse(event.data as string) as { type: string; [key: string]: unknown };
      switch (message.type) {
        case 'state':
          useLive.setState({ state: message.data as StateDto });
          break;
        case 'values': {
          const batch = message.data as LiveValue[];
          useLive.setState((s) => {
            const values = { ...s.values };
            for (const v of batch) values[v.id] = v;
            return { values };
          });
          break;
        }
        case 'catalog':
          void queryClient.invalidateQueries({ queryKey: queryKeys.sensors });
          break;
        case 'config':
          useLive.setState({ configRevision: message.revision as number });
          void queryClient.invalidateQueries({ queryKey: queryKeys.config });
          break;
        case 'notification':
          useLive.setState({ lastNotification: message.data as NotificationDto });
          break;
        case 'hello':
          useLive.setState({ configRevision: message.revision as number });
          break;
      }
    };

    ws.onclose = () => {
      socket = null;
      useLive.setState({ status: 'closed' });
      const delay = Math.min(10_000, 500 * 2 ** retry++);
      setTimeout(open, delay);
    };
  };

  open();
}
