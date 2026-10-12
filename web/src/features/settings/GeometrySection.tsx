import { Accordion, Button, Group, SimpleGrid, Stack, Text } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconAdjustmentsHorizontal } from '@tabler/icons-react';
import type { NeedleGeometry } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';
import { NumField } from './fields';
import { NeedleDiagram } from './NeedleDiagram';

/** Factory geometry, measured from the stock VU1 faces. */
export const DEFAULT_GEOMETRY: NeedleGeometry = { pivotX: 99.5, pivotY: 225.5, startAngle: -40, endAngle: 40 };

/** "Advanced" accordion: where the needle pivots and how far it swings. */
export function GeometrySection() {
  const { t } = useTranslation('settings');
  const geometry = useConfigDraft((s) => s.draft?.hardware.geometry);
  const update = useConfigDraft((s) => s.update);
  if (!geometry) return null;

  const set = (patch: Partial<NeedleGeometry>) =>
    update((d) => {
      Object.assign(d.hardware.geometry, patch);
    });
  const isDefault = (Object.keys(DEFAULT_GEOMETRY) as (keyof NeedleGeometry)[]).every((k) => geometry[k] === DEFAULT_GEOMETRY[k]);

  return (
    <Accordion variant="separated" radius="lg" chevronPosition="right">
      <Accordion.Item value="advanced">
        <Accordion.Control icon={<IconAdjustmentsHorizontal size={20} />}>
          <Text fw={600}>{t('advanced.title')}</Text>
          <Text size="sm" c="dimmed">
            {t('advanced.subtitle')}
          </Text>
        </Accordion.Control>
        <Accordion.Panel>
          <Group align="flex-start" gap="xl" wrap="wrap" pt="xs">
            <Stack gap="md" style={{ flex: '1 1 300px', minWidth: 0 }}>
              <Text size="sm">{t('advanced.explain')}</Text>
              <SimpleGrid cols={2} spacing="sm">
                <NumField
                  label={t('advanced.pivotX')}
                  description={t('advanced.pivotXHelp')}
                  value={geometry.pivotX}
                  min={-1000}
                  max={1000}
                  step={0.5}
                  decimalScale={2}
                  suffix=" px"
                  onCommit={(pivotX) => set({ pivotX })}
                />
                <NumField
                  label={t('advanced.pivotY')}
                  description={t('advanced.pivotYHelp')}
                  value={geometry.pivotY}
                  min={-1000}
                  max={1000}
                  step={0.5}
                  decimalScale={2}
                  suffix=" px"
                  onCommit={(pivotY) => set({ pivotY })}
                />
                <NumField
                  label={t('advanced.startAngle')}
                  description={t('advanced.startAngleHelp')}
                  value={geometry.startAngle}
                  min={-90}
                  max={90}
                  step={0.5}
                  decimalScale={2}
                  suffix="°"
                  error={geometry.startAngle >= geometry.endAngle ? t('advanced.angleOrder') : undefined}
                  onCommit={(startAngle) => {
                    if (startAngle < geometry.endAngle) set({ startAngle });
                  }}
                />
                <NumField
                  label={t('advanced.endAngle')}
                  description={t('advanced.endAngleHelp')}
                  value={geometry.endAngle}
                  min={-90}
                  max={90}
                  step={0.5}
                  decimalScale={2}
                  suffix="°"
                  onCommit={(endAngle) => {
                    if (endAngle > geometry.startAngle) set({ endAngle });
                  }}
                />
              </SimpleGrid>
              <Group>
                <Button variant="default" size="xs" disabled={isDefault} onClick={() => set(DEFAULT_GEOMETRY)}>
                  {t('advanced.reset')}
                </Button>
              </Group>
            </Stack>
            <div style={{ flex: '0 1 260px', width: '100%', maxWidth: 260 }}>
              <NeedleDiagram geometry={geometry} fraction={0.5} variant="full" label={t('advanced.diagram')} maxWidth={260} />
            </div>
          </Group>
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  );
}
