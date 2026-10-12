import { ActionIcon, Button, Paper, RangeSlider, Stack, TagsInput, Text, TextInput, Tooltip, Group } from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { ScaleLayer, ScaleZone, TickStyle, ZoneStyle } from '@/api/types';
import { NumField, PropGrid, Section, SegmentField, SelectField, SwitchField } from '../../components/fields';
import { defaultZone } from '../../model/layers';
import { FontFields, type LayerPropsOf } from './shared';

export function ScaleProps({ layer, patch }: LayerPropsOf<ScaleLayer>) {
  const { t } = useTranslation('face');
  const id = layer.id;
  const num = (key: keyof ScaleLayer, coalesce = true) => (v: number | undefined) => {
    if (v !== undefined) patch({ [key]: v } as Partial<ScaleLayer>, coalesce ? `${id}:${String(key)}` : undefined);
  };
  const setZone = (index: number, zone: Partial<ScaleZone>, coalesce?: string) =>
    patch({ zones: layer.zones.map((z, i) => (i === index ? { ...z, ...zone } : z)) }, coalesce);

  return (
    <>
      <Section title={t('props.ticks')}>
        <PropGrid>
          <NumField label={t('props.radius')} value={layer.radius} min={1} max={600} step={1} onChange={num('radius')} />
          <SegmentField<TickStyle>
            label={t('props.tickStyle')}
            value={layer.tickStyle}
            onChange={(tickStyle) => patch({ tickStyle })}
            data={(['line', 'dot', 'wedge'] as const).map((v) => ({ value: v, label: t(`tickStyles.${v}`) }))}
          />
          <NumField label={t('props.majorCount')} value={layer.majorCount} min={2} max={61} step={1} decimals={0} onChange={num('majorCount')} />
          <NumField label={t('props.minorPerMajor')} value={layer.minorPerMajor} min={0} max={99} step={1} decimals={0} onChange={num('minorPerMajor')} />
          <NumField label={t('props.majorLength')} value={layer.majorLength} min={0} max={200} step={0.5} onChange={num('majorLength')} />
          <NumField label={t('props.majorWidth')} value={layer.majorWidth} min={0} max={20} step={0.1} decimals={1} onChange={num('majorWidth')} />
          <NumField label={t('props.minorLength')} value={layer.minorLength} min={0} max={200} step={0.5} onChange={num('minorLength')} />
          <NumField label={t('props.minorWidth')} value={layer.minorWidth} min={0} max={20} step={0.1} decimals={1} onChange={num('minorWidth')} />
        </PropGrid>
        <SwitchField label={t('props.evenSpacing')} description={t('props.evenSpacingHint')} checked={layer.evenSpacing} onChange={(evenSpacing) => patch({ evenSpacing })} />
      </Section>

      <Section title={t('props.labels')} action={<SwitchField label="" ariaLabel={t('props.labels')} checked={layer.showLabels} onChange={(showLabels) => patch({ showLabels })} />}>
        {layer.showLabels && (
          <>
            <PropGrid>
              <NumField label={t('props.labelRadius')} value={layer.labelRadius} min={0} max={600} onChange={num('labelRadius')} />
              <NumField label={t('props.size')} value={layer.labelSize} min={1} max={200} suffix=" px" onChange={num('labelSize')} />
            </PropGrid>
            <FontFields
              font={layer.labelFont}
              weight={layer.labelWeight}
              onFont={(labelFont, labelWeight) => patch({ labelFont, labelWeight })}
              onWeight={(labelWeight) => patch({ labelWeight })}
            />
            <PropGrid>
              <TextInput
                size="xs"
                label={t('props.labelFormat')}
                placeholder={t('props.auto')}
                value={layer.labelFormat ?? ''}
                styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
                onChange={(e) => patch({ labelFormat: e.currentTarget.value || undefined }, `${id}:fmt`)}
              />
              <NumField label={t('props.labelDivisor')} value={layer.labelDivisor} min={0.000001} step={1} decimals={6} onChange={num('labelDivisor')} />
            </PropGrid>
            <TagsInput
              size="xs"
              label={t('props.customLabels')}
              description={t('props.customLabelsHint')}
              placeholder={layer.customLabels?.length ? undefined : t('props.customLabelsPlaceholder')}
              value={layer.customLabels ?? []}
              onChange={(v) => patch({ customLabels: v.length ? v : undefined })}
              clearable
            />
            <SwitchField label={t('props.rotateLabels')} checked={layer.rotateLabels} onChange={(rotateLabels) => patch({ rotateLabels })} />
          </>
        )}
      </Section>

      <Section title={t('props.arc')} action={<SwitchField label="" ariaLabel={t('props.arc')} checked={layer.showArc} onChange={(showArc) => patch({ showArc })} />}>
        {layer.showArc && (
          <PropGrid>
            <NumField
              label={t('props.arcRadius')}
              value={layer.arcRadius}
              nullable
              placeholder={String(layer.radius)}
              min={0}
              max={600}
              onChange={(v) => patch({ arcRadius: v }, `${id}:arcRadius`)}
            />
            <NumField label={t('props.arcWidth')} value={layer.arcWidth} min={0} max={50} step={0.5} decimals={1} onChange={num('arcWidth')} />
          </PropGrid>
        )}
      </Section>

      <Section
        title={t('props.zones')}
        action={
          <Button
            size="compact-xs"
            variant="light"
            leftSection={<IconPlus size={12} />}
            onClick={() => patch({ zones: [...layer.zones, defaultZone(layer.radius)] })}
          >
            {t('props.addZone')}
          </Button>
        }
      >
        {layer.zones.length === 0 && (
          <Text size="xs" c="dimmed">
            {t('props.noZones')}
          </Text>
        )}
        {layer.zones.map((zone, index) => (
          <Paper key={index} withBorder p="xs" radius="md">
            <Stack gap={8}>
              <Group gap="xs" wrap="nowrap" align="center">
                <RangeSlider
                  style={{ flex: 1 }}
                  size="sm"
                  min={0}
                  max={1}
                  step={0.01}
                  minRange={0.01}
                  value={[zone.from, zone.to]}
                  label={(v) => `${Math.round(v * 100)}%`}
                  onChange={([from, to]) => setZone(index, { from, to }, `${id}:zone${index}`)}
                  aria-label={t('props.zoneRange')}
                />
                <Text size="xs" ff="monospace" c="dimmed" w={70} ta="right">
                  {Math.round(zone.from * 100)}–{Math.round(zone.to * 100)}%
                </Text>
                <Tooltip label={t('props.removeZone')}>
                  <ActionIcon variant="subtle" color="red" size="sm" aria-label={t('props.removeZone')} onClick={() => patch({ zones: layer.zones.filter((_, i) => i !== index) })}>
                    <IconTrash size={14} />
                  </ActionIcon>
                </Tooltip>
              </Group>
              <PropGrid>
                <SelectField<ZoneStyle>
                  label={t('props.zoneStyle')}
                  value={zone.style}
                  onChange={(style) => setZone(index, { style })}
                  data={(['band', 'hatched', 'dotted', 'outline'] as const).map((v) => ({ value: v, label: t(`zoneStyles.${v}`) }))}
                />
                <div />
                <NumField label={t('props.innerRadius')} value={zone.innerRadius} min={0} max={600} onChange={(v) => v !== undefined && setZone(index, { innerRadius: v }, `${id}:zi${index}`)} />
                <NumField label={t('props.outerRadius')} value={zone.outerRadius} min={0} max={600} onChange={(v) => v !== undefined && setZone(index, { outerRadius: v }, `${id}:zo${index}`)} />
              </PropGrid>
            </Stack>
          </Paper>
        ))}
      </Section>
    </>
  );
}
