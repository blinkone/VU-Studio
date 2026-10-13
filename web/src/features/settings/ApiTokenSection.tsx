import { useState } from 'react';
import { ActionIcon, Alert, Button, Code, Group, SegmentedControl, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconEye, IconEyeOff, IconInfoCircle, IconKey, IconRefresh, IconSend } from '@tabler/icons-react';
import { useConfigDraft } from '@/state/configDraft';
import { CopyAction } from './CopyAction';
import { Section } from './Section';

/** 32 random hex characters (128 bits), generated in the browser. */
export function randomToken(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

const EXAMPLE_BODY = '{"sensors":[{"id":"demo/load","value":42,"name":"Demo load","unit":"%","min":0,"max":100}]}';

/** Bearer token for external programs, plus a ready-to-run example of the push API. */
export function ApiTokenSection() {
  const { t } = useTranslation('settings');
  const token = useConfigDraft((s) => s.draft?.web.apiToken ?? '');
  const savedToken = useConfigDraft((s) => s.base?.web.apiToken);
  const update = useConfigDraft((s) => s.update);
  const [reveal, setReveal] = useState(false);
  const [shell, setShell] = useState<'bash' | 'powershell'>('bash');

  const changed = savedToken !== undefined && savedToken !== token;
  const origin = window.location.origin;
  const shown = reveal ? token : '•'.repeat(Math.min(32, Math.max(token.length, 16)));

  const snippet = (secret: string) =>
    shell === 'bash'
      ? `curl -X POST "${origin}/api/push" \\\n  -H "Authorization: Bearer ${secret}" \\\n  -H "Content-Type: application/json" \\\n  -d '${EXAMPLE_BODY}'`
      : `Invoke-RestMethod -Method Post -Uri "${origin}/api/push" \`\n  -Headers @{ Authorization = "Bearer ${secret}" } \`\n  -ContentType "application/json" \`\n  -Body '${EXAMPLE_BODY}'`;

  return (
    <Section title={t('token.title')} description={t('token.description')} icon={<IconKey size={20} />}>
      <Stack gap="xs">
        <TextInput
          label={t('token.label')}
          readOnly
          value={shown}
          error={token.length < 16 ? t('token.tooShort') : undefined}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
          rightSectionWidth={72}
          rightSection={
            <Group gap={2} wrap="nowrap">
              <Tooltip label={reveal ? t('token.hide') : t('token.show')}>
                <ActionIcon variant="subtle" color="gray" onClick={() => setReveal((r) => !r)} aria-label={reveal ? t('token.hide') : t('token.show')}>
                  {reveal ? <IconEyeOff size={16} /> : <IconEye size={16} />}
                </ActionIcon>
              </Tooltip>
              <CopyAction value={token} label={t('token.copy')} />
            </Group>
          }
        />
        <Group>
          <Button
            variant="default"
            size="xs"
            leftSection={<IconRefresh size={15} />}
            onClick={() =>
              update((d) => {
                d.web.apiToken = randomToken();
              })
            }
          >
            {t('token.regenerate')}
          </Button>
        </Group>
        <Text size="xs" c="dimmed">
          {t('token.regenerateHelp')}
        </Text>
        {changed && (
          <Alert variant="light" color="yellow" icon={<IconInfoCircle size={18} />} p="xs">
            <Text size="sm">{t('token.unsaved')}</Text>
          </Alert>
        )}
      </Stack>

      <Stack gap="xs">
        <Group justify="space-between" wrap="wrap">
          <Group gap={6}>
            <IconSend size={16} aria-hidden />
            <Text fw={600} size="sm">
              {t('token.pushTitle')}
            </Text>
          </Group>
          <SegmentedControl
            size="xs"
            value={shell}
            onChange={(value) => setShell(value as 'bash' | 'powershell')}
            data={[
              { value: 'bash', label: 'curl' },
              { value: 'powershell', label: 'PowerShell' },
            ]}
          />
        </Group>
        <Text size="xs" c="dimmed">
          {t('token.pushHelp')}
        </Text>
        <div style={{ position: 'relative' }}>
          <Code block style={{ paddingRight: 44, fontSize: 12, overflowX: 'auto' }} tabIndex={0} aria-label={t('token.pushTitle')}>
            {snippet(reveal ? token : 'YOUR_TOKEN')}
          </Code>
          <div style={{ position: 'absolute', top: 6, right: 6 }}>
            <CopyAction size="sm" value={snippet(token)} label={t('token.copySnippet')} />
          </div>
        </div>
        <Text size="xs" c="dimmed">
          {t('token.pushIds')}
        </Text>
      </Stack>
    </Section>
  );
}
