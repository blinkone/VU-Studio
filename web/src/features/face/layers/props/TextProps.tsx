import { Group } from '@mantine/core';
import {
  IconAlignCenter,
  IconAlignLeft,
  IconAlignRight,
  IconLayoutAlignBottom,
  IconLayoutAlignMiddle,
  IconLayoutAlignTop,
  IconBaseline,
} from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { TextAlign, TextBaseline, TextLayer, TextOverflow } from '@/api/types';
import { NumField, PropGrid, Section, SegmentField, SelectField, SwitchField } from '../../components/fields';
import { TemplateInput } from '../../components/TemplateInput';
import { FontFields, PositionFields, type LayerPropsOf } from './shared';

export function TextProps({ layer, patch }: LayerPropsOf<TextLayer>) {
  const { t } = useTranslation('face');
  const id = layer.id;
  return (
    <>
      <Section title={t('props.content')}>
        <TemplateInput size="xs" value={layer.text} onChange={(v) => patch({ text: v }, `${id}:text`)} ariaLabel={t('props.text')} />
        <Group gap="lg">
          <SwitchField label={t('props.uppercase')} checked={layer.uppercase} onChange={(v) => patch({ uppercase: v })} />
          <SwitchField label={t('props.italic')} checked={layer.italic} onChange={(v) => patch({ italic: v })} />
        </Group>
      </Section>

      <Section title={t('props.typography')}>
        <FontFields
          font={layer.font}
          weight={layer.weight}
          onFont={(font, weight) => patch({ font, weight })}
          onWeight={(weight) => patch({ weight })}
        />
        <PropGrid>
          <NumField label={t('props.size')} value={layer.size} min={1} max={400} step={1} suffix=" px" onChange={(v) => v !== undefined && patch({ size: v }, `${id}:size`)} />
          <NumField label={t('props.letterSpacing')} value={layer.letterSpacing} min={-20} max={50} step={0.1} decimals={1} onChange={(v) => v !== undefined && patch({ letterSpacing: v }, `${id}:ls`)} />
        </PropGrid>
        <SwitchField label={t('props.smooth')} description={t('props.smoothHint')} checked={layer.smooth} onChange={(v) => patch({ smooth: v })} />
      </Section>

      <Section title={t('props.layout')}>
        <PositionFields id={id} x={layer.x} y={layer.y} onChange={patch} />
        <PropGrid>
          <SegmentField<TextAlign>
            label={t('props.align')}
            value={layer.align}
            onChange={(align) => patch({ align })}
            data={[
              { value: 'left', label: t('props.alignLeft'), icon: IconAlignLeft },
              { value: 'center', label: t('props.alignCenter'), icon: IconAlignCenter },
              { value: 'right', label: t('props.alignRight'), icon: IconAlignRight },
            ]}
          />
          <SegmentField<TextBaseline>
            label={t('props.baseline')}
            value={layer.baseline}
            onChange={(baseline) => patch({ baseline })}
            data={[
              { value: 'top', label: t('props.baselineTop'), icon: IconLayoutAlignTop },
              { value: 'middle', label: t('props.baselineMiddle'), icon: IconLayoutAlignMiddle },
              { value: 'baseline', label: t('props.baselineBaseline'), icon: IconBaseline },
              { value: 'bottom', label: t('props.baselineBottom'), icon: IconLayoutAlignBottom },
            ]}
          />
        </PropGrid>
        <PropGrid>
          <NumField
            label={t('props.maxWidth')}
            value={layer.maxWidth}
            nullable
            placeholder={t('props.none')}
            min={1}
            max={400}
            onChange={(v) => patch({ maxWidth: v }, `${id}:maxWidth`)}
          />
          <SelectField<TextOverflow>
            label={t('props.overflow')}
            value={layer.overflow}
            disabled={layer.maxWidth === undefined}
            onChange={(overflow) => patch({ overflow })}
            data={(['shrink', 'ellipsis', 'clip', 'visible'] as const).map((o) => ({ value: o, label: t(`overflow.${o}`) }))}
          />
        </PropGrid>
        <NumField label={t('props.rotation')} value={layer.rotation} min={-360} max={360} step={1} suffix="°" onChange={(v) => v !== undefined && patch({ rotation: v }, `${id}:rot`)} />
      </Section>
    </>
  );
}
