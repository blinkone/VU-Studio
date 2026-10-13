import type { ReactNode } from 'react';
import { Alert, Anchor, Badge, Code, Group, Skeleton, Stack, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconBook, IconExternalLink, IconHeart, IconInfoCircle, IconScale } from '@tabler/icons-react';
import { useFonts, useInfo } from '@/api/queries';
import { Logo } from '@/components/layout/Logo';
import { CopyAction } from './CopyAction';
import { Section } from './Section';

const REPO_URL = 'https://github.com/blinkone/VuDials';
const DOCS_URL = `${REPO_URL}/tree/main/docs`;

function External({ href, children }: { href: string; children: ReactNode }) {
  return (
    <Anchor href={href} target="_blank" rel="noopener noreferrer" inline>
      {children} <IconExternalLink size={13} style={{ verticalAlign: '-1px' }} aria-hidden />
    </Anchor>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Group gap="md" wrap="nowrap" align="flex-start">
      <Text size="sm" c="dimmed" w={130} style={{ flex: 'none' }}>
        {label}
      </Text>
      <div style={{ minWidth: 0, flex: 1, overflowWrap: 'anywhere' }}>{children}</div>
    </Group>
  );
}

/** Settings → About. */
export function AboutTab() {
  const { t } = useTranslation('settings');
  const { data: info } = useInfo();
  const { data: fonts } = useFonts();
  const families = [...new Set((fonts ?? []).map((f) => f.family))].sort((a, b) => a.localeCompare(b));

  return (
    <Stack gap="lg">
      <Section title={t('about.title')} icon={<IconInfoCircle size={20} />}>
        <Logo />
        {info ? (
          <Stack gap="xs">
            <Row label={t('about.version')}>
              <Badge variant="light">{info.version}</Badge>
            </Row>
            <Row label={t('about.os')}>
              <Text size="sm">{info.os}</Text>
            </Row>
            <Row label={t('about.machine')}>
              <Text size="sm">{info.machine}</Text>
            </Row>
            {info.dataDirectory && (
              <Row label={t('about.dataDirectory')}>
                <Group gap={4} wrap="nowrap" align="flex-start">
                  <Code>{info.dataDirectory}</Code>
                  <CopyAction size="sm" value={info.dataDirectory} label={t('about.copyPath')} />
                </Group>
              </Row>
            )}
            {info.simulate > 0 && (
              <Row label={t('about.simulator')}>
                <Text size="sm">{t('about.simulatorValue', { count: info.simulate })}</Text>
              </Row>
            )}
          </Stack>
        ) : (
          <Skeleton height={90} />
        )}
      </Section>

      <Section title={t('about.licenseTitle')} icon={<IconScale size={20} />}>
        <Text size="sm">{t('about.license')}</Text>
        <Group gap="lg">
          <External href="https://www.gnu.org/licenses/gpl-3.0.html">GPL-3.0</External>
          <External href={REPO_URL}>{t('about.source')}</External>
        </Group>
        <Group gap={6}>
          <IconBook size={16} aria-hidden />
          <External href={DOCS_URL}>{t('about.docs')}</External>
        </Group>
      </Section>

      <Section title={t('about.creditsTitle')} icon={<IconHeart size={20} />}>
        <Stack gap="xs">
          <Text size="sm">
            {t('about.fonts')}{' '}
            <External href="https://openfontlicense.org/">OFL</External>
          </Text>
          {families.length > 0 && (
            <Text size="xs" c="dimmed">
              {families.join(' · ')}
            </Text>
          )}
          <Text size="sm" mt="xs">
            {t('about.icons')}{' '}
            <External href="https://pictogrammers.com/library/mdi/">Material Design Icons</External>
          </Text>
        </Stack>
        <Alert variant="light" color="gray" p="sm">
          <Text size="sm">{t('about.disclaimer')}</Text>
        </Alert>
      </Section>
    </Stack>
  );
}
