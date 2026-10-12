import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useShallow } from 'zustand/react/shallow';
import { useLive } from '@/api/live';
import { useSensors } from '@/api/queries';
import type { DialPage, FaceConfig, SensorDto } from '@/api/types';
import { formatNumber } from '@/lib/format';
import type { SampleContext } from './textMetrics';

export interface PageInfo {
  /** The page's sensor, when it is in the catalog. */
  sensor: SensorDto | undefined;
  /** What {title} shows when the face has no title of its own. */
  defaultTitle: string;
  /** What {icon} shows when the face has no icon of its own. */
  defaultIcon: string | undefined;
  /** Display range of the binding, for threshold tints. */
  min: number;
  max: number;
  sample: SampleContext;
}

/** The slot's live value fields (not the needle, which moves constantly), when it shows `pageId`. */
function useLiveValue(slot: number, pageId: string) {
  return useLive(
    useShallow((s) => {
      const dial = s.state?.engine.dials.find((d) => d.slot === slot);
      if (!dial || dial.pageId !== pageId) return { live: false, value: undefined, valueText: undefined, unit: undefined };
      return { live: true, value: dial.value, valueText: dial.valueText, unit: dial.unit };
    }),
  );
}

/**
 * Context about the edited page: default title/icon (same fallbacks as the service's DialEngine) and
 * sample values for placeholders, taken from the live dial state when it shows this page.
 */
export function usePageInfo(slot: number, page: DialPage, face: FaceConfig): PageInfo {
  const { t, i18n } = useTranslation('face');
  const { data: sensors } = useSensors();
  const live = useLiveValue(slot, page.id);

  return useMemo(() => {
    const byId = new Map((sensors ?? []).map((s) => [s.id, s]));
    const sensor = page.value.sensorId ? byId.get(page.value.sensorId) : undefined;
    const defaultTitle =
      [page.value.label, sensor?.name, page.name].find((v) => v && v.trim()) ?? t('stage.dialTitle', { slot });
    const min = page.value.min ?? sensor?.min ?? 0;
    const max = page.value.max ?? sensor?.max ?? 100;
    const number = (v: number, decimals?: number) => formatNumber(v, decimals, i18n.language);
    const value =
      live.valueText ??
      (live.value !== undefined && live.value !== null
        ? number(live.value, page.value.decimals)
        : sensor?.value !== undefined
          ? number(sensor.value, page.value.decimals)
          : number(min + (max - min) * 0.88, 0));
    const sample: SampleContext = {
      title: face.title?.trim() ? face.title : defaultTitle,
      subtitle: face.subtitle ?? '',
      value,
      unit: live.unit ?? sensor?.unit ?? '',
      min: number(min, Number.isInteger(min) ? 0 : undefined),
      max: number(max, Number.isInteger(max) ? 0 : undefined),
      percent: '88',
      sensors: byId,
      locale: i18n.language,
    };
    return { sensor, defaultTitle, defaultIcon: sensor?.icon, min, max, sample };
  }, [sensors, page, face.title, face.subtitle, live, slot, t, i18n.language]);
}
