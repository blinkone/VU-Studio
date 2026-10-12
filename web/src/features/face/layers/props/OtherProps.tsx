import { Group } from '@mantine/core';
import { IconCircle, IconLine, IconSquare, IconChartArcs } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { SensorPicker } from '@/components/SensorPicker';
import type { BarLayer, IconLayer, Orientation, ShapeKind, ShapeLayer, SparklineLayer, SparklineStyle } from '@/api/types';
import { IconPicker } from '../../components/IconPicker';
import { NumField, PropGrid, Section, SegmentField, SwitchField } from '../../components/fields';
import { BoxFields, PositionFields, type LayerPropsOf } from './shared';

const numericSensor = (s: { valueKind: string }) => s.valueKind === 'number';

export function IconProps({ layer, patch, pageIcon }: LayerPropsOf<IconLayer> & { pageIcon: string | undefined }) {
  const { t } = useTranslation('face');
  return (
    <Section title={t('props.icon')}>
      <IconPicker
        size="xs"
        value={layer.icon}
        automatic={{ value: '{icon}', label: t('props.pageIcon'), resolved: pageIcon }}
        onChange={(icon) => patch({ icon: icon ?? '{icon}' })}
      />
      <PositionFields id={layer.id} x={layer.x} y={layer.y} onChange={patch} />
      <NumField label={t('props.size')} description={t('props.iconSizeHint')} value={layer.size} min={1} max={400} suffix=" px" onChange={(v) => v !== undefined && patch({ size: v }, `${layer.id}:size`)} />
    </Section>
  );
}

export function ShapeProps({ layer, patch }: LayerPropsOf<ShapeLayer>) {
  const { t } = useTranslation('face');
  const id = layer.id;
  const line = layer.kind === 'line';
  return (
    <>
      <Section title={t('props.shape')}>
        <SegmentField<ShapeKind>
          value={layer.kind}
          onChange={(kind) => patch({ kind })}
          data={[
            { value: 'rectangle', label: t('shapes.rectangle'), icon: IconSquare },
            { value: 'ellipse', label: t('shapes.ellipse'), icon: IconCircle },
            { value: 'line', label: t('shapes.line'), icon: IconLine },
            { value: 'arc', label: t('shapes.arc'), icon: IconChartArcs },
          ]}
        />
        {line ? (
          <>
            <PositionFields id={id} x={layer.x} y={layer.y} onChange={patch} />
            <PropGrid>
              <NumField axis="X2" value={layer.x2} onChange={(v) => v !== undefined && patch({ x2: v }, `${id}:x2`)} />
              <NumField axis="Y2" value={layer.y2} onChange={(v) => v !== undefined && patch({ y2: v }, `${id}:y2`)} />
            </PropGrid>
          </>
        ) : (
          <BoxFields layer={layer} patch={patch} />
        )}
        {layer.kind === 'rectangle' && (
          <NumField label={t('props.cornerRadius')} value={layer.cornerRadius} min={0} max={100} step={0.5} decimals={1} onChange={(v) => v !== undefined && patch({ cornerRadius: v }, `${id}:corner`)} />
        )}
        {layer.kind === 'arc' && (
          <PropGrid>
            <NumField label={t('props.startAngle')} description={t('props.startAngleHint')} value={layer.startAngle} min={-360} max={360} suffix="°" onChange={(v) => v !== undefined && patch({ startAngle: v }, `${id}:start`)} />
            <NumField label={t('props.sweepAngle')} value={layer.sweepAngle} min={-360} max={360} suffix="°" onChange={(v) => v !== undefined && patch({ sweepAngle: v }, `${id}:sweep`)} />
          </PropGrid>
        )}
      </Section>
      <Section title={t('props.style')}>
        <Group gap="lg">
          <SwitchField label={t('props.stroke')} checked={layer.stroke} onChange={(stroke) => patch({ stroke })} />
          {!line && <SwitchField label={t('props.fill')} checked={layer.fill} onChange={(fill) => patch({ fill })} />}
          <SwitchField label={t('props.dashed')} checked={layer.dashed} onChange={(dashed) => patch({ dashed })} />
        </Group>
        <NumField label={t('props.strokeWidth')} value={layer.strokeWidth} min={0} max={100} step={0.5} decimals={1} suffix=" px" disabled={!layer.stroke} onChange={(v) => v !== undefined && patch({ strokeWidth: v }, `${id}:sw`)} />
      </Section>
    </>
  );
}

function DataFields({ layer, patch: p }: { layer: BarLayer | SparklineLayer; patch: (patch: { sensorId?: string; min?: number; max?: number }, coalesce?: string) => void }) {
  const { t } = useTranslation('face');
  return (
    <>
      <SensorPicker
        size="xs"
        label={t('props.sensor')}
        placeholder={t('props.pageValue')}
        value={layer.sensorId}
        sensorFilter={numericSensor}
        onChange={(sensorId) => p({ sensorId })}
      />
      <PropGrid>
        <NumField label={t('props.min')} value={layer.min} nullable placeholder={t('props.auto')} onChange={(v) => p({ min: v }, `${layer.id}:min`)} />
        <NumField label={t('props.max')} value={layer.max} nullable placeholder={t('props.auto')} onChange={(v) => p({ max: v }, `${layer.id}:max`)} />
      </PropGrid>
    </>
  );
}

export function BarProps({ layer, patch }: LayerPropsOf<BarLayer>) {
  const { t } = useTranslation('face');
  return (
    <>
      <Section title={t('props.data')}>
        <DataFields layer={layer} patch={patch} />
      </Section>
      <Section title={t('props.layout')}>
        <BoxFields layer={layer} patch={patch} />
        <SegmentField<Orientation>
          label={t('props.orientation')}
          value={layer.orientation}
          onChange={(orientation) => patch({ orientation })}
          data={(['horizontal', 'vertical'] as const).map((v) => ({ value: v, label: t(`orientations.${v}`) }))}
        />
      </Section>
      <Section title={t('props.style')}>
        <PropGrid>
          <NumField label={t('props.segments')} description={t('props.segmentsHint')} value={layer.segments} min={0} max={100} decimals={0} onChange={(v) => v !== undefined && patch({ segments: v }, `${layer.id}:seg`)} />
          <NumField label={t('props.gap')} description=" " value={layer.gap} min={0} max={20} step={0.5} decimals={1} disabled={layer.segments === 0} onChange={(v) => v !== undefined && patch({ gap: v }, `${layer.id}:gap`)} />
        </PropGrid>
        <SwitchField label={t('props.border')} checked={layer.border} onChange={(border) => patch({ border })} />
      </Section>
    </>
  );
}

export function SparklineProps({ layer, patch }: LayerPropsOf<SparklineLayer>) {
  const { t } = useTranslation('face');
  return (
    <>
      <Section title={t('props.data')}>
        <DataFields layer={layer} patch={patch} />
        <NumField label={t('props.window')} value={layer.windowSeconds} min={10} max={3600} step={30} decimals={0} suffix=" s" description={t('props.windowHint')} onChange={(v) => v !== undefined && patch({ windowSeconds: v }, `${layer.id}:win`)} />
      </Section>
      <Section title={t('props.layout')}>
        <BoxFields layer={layer} patch={patch} />
      </Section>
      <Section title={t('props.style')}>
        <SegmentField<SparklineStyle>
          value={layer.style}
          onChange={(style) => patch({ style })}
          data={(['line', 'area', 'bars'] as const).map((v) => ({ value: v, label: t(`sparkStyles.${v}`) }))}
        />
        <NumField label={t('props.strokeWidth')} value={layer.strokeWidth} min={1} max={8} step={0.5} decimals={1} suffix=" px" onChange={(v) => v !== undefined && patch({ strokeWidth: v }, `${layer.id}:sw`)} />
        <SwitchField label={t('props.border')} checked={layer.border} onChange={(border) => patch({ border })} />
      </Section>
    </>
  );
}
