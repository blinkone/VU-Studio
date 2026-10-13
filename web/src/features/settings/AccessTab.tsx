import { useState } from 'react';
import { Alert, Badge, Code, Divider, Group, Stack, Switch, TagsInput, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconInfoCircle, IconServer, IconShieldLock, IconWorld } from '@tabler/icons-react';
import { useInfo } from '@/api/queries';
import { useConfigDraft } from '@/state/configDraft';
import { ApiTokenSection } from './ApiTokenSection';
import { CopyAction } from './CopyAction';
import { NumField } from './fields';
import { PasswordForm } from './PasswordForm';
import { Section } from './Section';

const HOST_PATTERN = /^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$/;

/** Accepts what people paste ("http://nas.lan:5341/") and keeps the bare host name the service compares. */
function normalizeHost(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/^[a-z]+:\/\//, '')
    .replace(/[/?#].*$/, '')
    .replace(/:\d+$/, '');
}

/** Settings → Access & API. */
export function AccessTab() {
  const { t } = useTranslation('settings');
  const { data: info } = useInfo();
  const web = useConfigDraft((s) => s.draft?.web);
  const update = useConfigDraft((s) => s.update);
  const [hostError, setHostError] = useState<string | undefined>();
  if (!web) return null;

  const passwordSet = !!web.passwordHash;
  const local = info?.local ?? false;
  const lanUrl = `http://${info?.machine ?? 'this-pc'}:${web.port}`;

  return (
    <Stack gap="lg">
      <Section title={t('access.title')} description={t('access.description')} icon={<IconWorld size={20} />}>
        <NumField
          label={t('access.port')}
          description={t('access.portHelp')}
          maw={260}
          min={1024}
          max={65535}
          allowDecimal={false}
          thousandSeparator=""
          value={web.port}
          onCommit={(port) =>
            update((d) => {
              d.web.port = port;
            })
          }
        />

        <Divider />

        <Switch
          checked={web.allowLan}
          disabled={!passwordSet}
          onChange={(event) => {
            const allowLan = event.currentTarget.checked;
            update((d) => {
              d.web.allowLan = allowLan;
            });
          }}
          label={t('access.lan')}
          description={passwordSet ? t('access.lanHelp') : t('access.lanNeedsPassword')}
        />
        {web.allowLan && (
          <Group gap="xs" wrap="nowrap">
            <Text size="sm" c="dimmed">
              {t('access.lanUrl')}
            </Text>
            <Code>{lanUrl}</Code>
            <CopyAction size="sm" value={lanUrl} label={t('access.copyUrl')} />
          </Group>
        )}
        <Alert variant="light" color="gray" icon={<IconInfoCircle size={18} />} p="sm">
          <Text size="sm">{t('access.restartNote')}</Text>
        </Alert>

        <Divider />

        <Group gap="xs">
          <IconShieldLock size={18} aria-hidden />
          <Text fw={600}>{t('password.title')}</Text>
          <Badge variant="light" color={passwordSet ? 'teal' : 'gray'}>
            {passwordSet ? t('password.isSet') : t('password.notSet')}
          </Badge>
        </Group>
        {local ? (
          <PasswordForm passwordSet={passwordSet} />
        ) : (
          <Alert variant="light" color="gray" icon={<IconInfoCircle size={18} />} p="sm">
            <Text size="sm">{t('password.localOnly')}</Text>
          </Alert>
        )}
      </Section>

      <ApiTokenSection />

      <Section title={t('hosts.title')} description={t('hosts.description')} icon={<IconServer size={20} />}>
        <TagsInput
          label={t('hosts.label')}
          description={t('hosts.help')}
          placeholder={web.allowedHosts.length === 0 ? 'vustudio.home.lan' : undefined}
          splitChars={[',', ' ', ';']}
          clearable
          value={web.allowedHosts}
          error={hostError}
          onChange={(values) => {
            const hosts = [...new Set(values.map(normalizeHost).filter(Boolean))];
            const invalid = hosts.filter((h) => !HOST_PATTERN.test(h));
            setHostError(invalid.length > 0 ? t('hosts.invalid', { names: invalid.join(', ') }) : undefined);
            update((d) => {
              d.web.allowedHosts = hosts.filter((h) => HOST_PATTERN.test(h));
            });
          }}
        />
      </Section>
    </Stack>
  );
}
