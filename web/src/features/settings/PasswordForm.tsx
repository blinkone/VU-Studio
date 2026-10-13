import { useState } from 'react';
import { Alert, Button, Group, Modal, PasswordInput, Stack, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconInfoCircle, IconLockOff } from '@tabler/icons-react';
import { api } from '@/api/client';
import { adoptServerWebAuth, runAction } from './actions';

const MIN_PASSWORD = 8;

/** Sets, changes or removes the LAN password (`POST /api/auth/password`, only accepted from this PC). */
export function PasswordForm({ passwordSet }: { passwordSet: boolean }) {
  const { t } = useTranslation('settings');
  const { t: tc } = useTranslation('common');
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [busy, setBusy] = useState<'set' | 'remove' | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);

  const tooShort = password.length > 0 && password.length < MIN_PASSWORD;
  const mismatch = repeat.length > 0 && repeat !== password;
  const canSubmit = password.length >= MIN_PASSWORD && password === repeat;

  const send = async (value: string, kind: 'set' | 'remove') => {
    setBusy(kind);
    const ok = await runAction(() => api.post('/api/auth/password', { password: value }), {
      success: kind === 'set' ? t('password.saved') : t('password.removed'),
      error: t('password.failed'),
    });
    if (ok) {
      setPassword('');
      setRepeat('');
      await adoptServerWebAuth();
    }
    setBusy(null);
    setConfirmRemove(false);
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (canSubmit) void send(password, 'set');
      }}
    >
      <Stack gap="sm">
        <Text fw={600} size="sm">
          {passwordSet ? t('password.changeTitle') : t('password.setTitle')}
        </Text>
        <PasswordInput
          label={t('password.new')}
          autoComplete="new-password"
          maw={360}
          value={password}
          error={tooShort ? t('password.tooShort', { min: MIN_PASSWORD }) : undefined}
          onChange={(event) => setPassword(event.currentTarget.value)}
        />
        <PasswordInput
          label={t('password.repeat')}
          autoComplete="new-password"
          maw={360}
          value={repeat}
          error={mismatch ? t('password.mismatch') : undefined}
          onChange={(event) => setRepeat(event.currentTarget.value)}
        />
        <Group gap="sm">
          <Button type="submit" disabled={!canSubmit || busy === 'remove'} loading={busy === 'set'}>
            {passwordSet ? t('password.change') : t('password.set')}
          </Button>
          {passwordSet && (
            <Button variant="subtle" color="red" leftSection={<IconLockOff size={16} />} onClick={() => setConfirmRemove(true)}>
              {t('password.remove')}
            </Button>
          )}
        </Group>
      </Stack>

      <Modal opened={confirmRemove} onClose={() => setConfirmRemove(false)} title={t('password.removeTitle')} centered>
        <Stack>
          <Alert variant="light" color="yellow" icon={<IconInfoCircle size={18} />} p="sm">
            <Text size="sm">{t('password.removeHelp')}</Text>
          </Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirmRemove(false)}>
              {tc('actions.cancel')}
            </Button>
            <Button color="red" loading={busy === 'remove'} onClick={() => void send('', 'remove')}>
              {t('password.remove')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </form>
  );
}
