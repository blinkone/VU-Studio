import { notifications } from '@mantine/notifications';
import { api, ApiError } from '@/api/client';
import type { AppConfig } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';

/** Readable text for a failed request (validation errors are joined, the rest uses the message). */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.errors.length > 0 ? error.errors.join('\n') : error.message;
  return error instanceof Error ? error.message : String(error);
}

/**
 * Runs an API action and reports the outcome with a notification.
 * Returns whether it succeeded, so callers can react without their own try/catch.
 */
export async function runAction(
  run: () => Promise<unknown>,
  messages: { success?: string; error: string },
): Promise<boolean> {
  try {
    await run();
    if (messages.success) notifications.show({ color: 'teal', message: messages.success });
    return true;
  } catch (error) {
    notifications.show({ color: 'red', title: messages.error, message: errorMessage(error) });
    return false;
  }
}

/**
 * `POST /api/auth/password` changes the stored configuration behind the draft's back: the revision
 * moves on, the password hash changes and removing the password also switches LAN access off.
 * Adopt that state. Unsaved edits are kept; without edits the draft is simply reloaded.
 */
export async function adoptServerWebAuth(): Promise<void> {
  const store = useConfigDraft.getState();
  if (!store.dirty) {
    await store.load();
    return;
  }

  const fresh = await api.get<AppConfig>('/api/config');
  const adopt = (config: AppConfig, keepLanChoice: boolean): AppConfig => ({
    ...config,
    revision: fresh.revision,
    web: {
      ...config.web,
      passwordHash: fresh.web.passwordHash,
      allowLan: keepLanChoice && fresh.web.passwordHash ? config.web.allowLan : fresh.web.allowLan,
    },
  });
  useConfigDraft.setState((s) => ({
    base: s.base ? adopt(s.base, false) : s.base,
    draft: s.draft ? adopt(s.draft, true) : s.draft,
  }));
}
