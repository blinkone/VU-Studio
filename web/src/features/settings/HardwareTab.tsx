import { useState } from 'react';
import { Group, Slider, Stack, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconSun, IconSunLow } from '@tabler/icons-react';
import { useConfigDraft } from '@/state/configDraft';
import { CalibrationWizard } from './CalibrationWizard';
import { DialsSection } from './DialsSection';
import { GeometrySection } from './GeometrySection';
import { HubSection } from './HubSection';
import { Section } from './Section';

/** Settings → Hardware: hub, dials, master brightness and the advanced needle geometry. */
export function HardwareTab() {
  const [wizard, setWizard] = useState<{ uid?: string } | null>(null);

  return (
    <Stack gap="lg">
      <HubSection />
      <DialsSection onCalibrate={(uid) => setWizard({ uid })} />
      <BrightnessSection />
      <GeometrySection />
      {wizard && <CalibrationWizard initialUid={wizard.uid} onClose={() => setWizard(null)} />}
    </Stack>
  );
}

function BrightnessSection() {
  const { t } = useTranslation('settings');
  const brightness = useConfigDraft((s) => s.draft?.hardware.masterBrightness ?? 100);
  const update = useConfigDraft((s) => s.update);

  return (
    <Section title={t('brightness.title')} description={t('brightness.description')} icon={<IconSun size={20} />}>
      <Group gap="md" wrap="nowrap">
        <IconSunLow size={20} aria-hidden style={{ flex: 'none', opacity: 0.7 }} />
        <Slider
          style={{ flex: 1 }}
          min={0}
          max={100}
          step={1}
          value={brightness}
          label={(value) => `${value}%`}
          thumbLabel={t('brightness.title')}
          marks={[{ value: 0 }, { value: 25 }, { value: 50 }, { value: 75 }, { value: 100 }]}
          onChange={(value) =>
            update((d) => {
              d.hardware.masterBrightness = value;
            })
          }
        />
        <IconSun size={20} aria-hidden style={{ flex: 'none', opacity: 0.7 }} />
        <Text ff="monospace" fw={600} w={48} ta="right">
          {brightness}%
        </Text>
      </Group>
    </Section>
  );
}
