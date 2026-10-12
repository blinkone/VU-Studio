import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useFonts } from '@/api/queries';
import type { FaceLayer, FontInfo } from '@/api/types';
import { NumField, PropGrid, SelectField } from '../../components/fields';

/** Applies a partial change to the layer; edits with the same `coalesce` key form one undo step. */
export type Patch<T extends FaceLayer> = (patch: Partial<T>, coalesce?: string) => void;

export interface LayerPropsOf<T extends FaceLayer> {
  layer: T;
  patch: Patch<T>;
}

const WEIGHT_NAMES: Record<number, string> = {
  100: 'thin',
  200: 'extraLight',
  300: 'light',
  400: 'regular',
  500: 'medium',
  600: 'semibold',
  700: 'bold',
  800: 'extraBold',
  900: 'black',
};

/** Closest weight a font provides. */
export function nearestWeight(font: FontInfo | undefined, weight: number): number {
  if (!font || font.weights.length === 0) return weight;
  return font.weights.reduce((best, w) => (Math.abs(w - weight) < Math.abs(best - weight) ? w : best), font.weights[0]);
}

/** Font family + weight selectors fed by the service's font catalog. */
export function FontFields({ font, weight, onFont, onWeight }: { font: string; weight: number; onFont: (font: string, weight: number) => void; onWeight: (weight: number) => void }) {
  const { t } = useTranslation('face');
  const { data: fonts = [] } = useFonts();
  const current = fonts.find((f) => f.id === font);

  const fontData = useMemo(() => {
    const groups = new Map<string, { value: string; label: string }[]>();
    for (const f of fonts) {
      const g = t(`fontCategories.${f.category}`, { defaultValue: f.category });
      if (!groups.has(g)) groups.set(g, []);
      groups.get(g)!.push({ value: f.id, label: f.family });
    }
    if (!fonts.some((f) => f.id === font)) groups.set('?', [{ value: font, label: font }]);
    return [...groups.entries()].map(([group, items]) => ({ group, items }));
  }, [fonts, font, t]);

  const weights = current?.weights.length ? current.weights : [weight];

  return (
    <PropGrid>
      <SelectField
        label={t('props.font')}
        value={font}
        data={fontData}
        onChange={(id) => onFont(id, nearestWeight(fonts.find((f) => f.id === id), weight))}
      />
      <SelectField
        label={t('props.weight')}
        value={String(weight)}
        data={weights.map((w) => ({ value: String(w), label: `${w} · ${t(`weights.${WEIGHT_NAMES[w] ?? 'regular'}`)}` }))}
        onChange={(v) => onWeight(Number(v))}
      />
    </PropGrid>
  );
}

/** X / Y position inputs. */
export function PositionFields({ x, y, onChange, id }: { x: number; y: number; onChange: (p: { x?: number; y?: number }, coalesce: string) => void; id: string }) {
  return (
    <PropGrid>
      <NumField axis="X" value={x} onChange={(v) => v !== undefined && onChange({ x: v }, `${id}:x`)} step={1} />
      <NumField axis="Y" value={y} onChange={(v) => v !== undefined && onChange({ y: v }, `${id}:y`)} step={1} />
    </PropGrid>
  );
}

/** X / Y / W / H inputs for box layers. */
export function BoxFields({ layer, patch }: { layer: Extract<FaceLayer, { width: number }>; patch: (p: { x?: number; y?: number; width?: number; height?: number }, coalesce: string) => void }) {
  const { t } = useTranslation('face');
  return (
    <>
      <PositionFields id={layer.id} x={layer.x} y={layer.y} onChange={patch} />
      <PropGrid>
        <NumField axis="W" ariaLabel={t('props.width')} value={layer.width} min={1} onChange={(v) => v !== undefined && patch({ width: v }, `${layer.id}:w`)} />
        <NumField axis="H" ariaLabel={t('props.height')} value={layer.height} min={1} onChange={(v) => v !== undefined && patch({ height: v }, `${layer.id}:h`)} />
      </PropGrid>
    </>
  );
}
