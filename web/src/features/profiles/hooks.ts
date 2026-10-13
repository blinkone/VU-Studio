import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSensors } from '@/api/queries';
import type { SensorDto } from '@/api/types';
import type { Translate } from './profileModel';

/** Sensor catalog lookups used to turn ids into readable text. */
export function useSensorLookup() {
  const { data: sensors } = useSensors();
  return useMemo(() => {
    const byId = new Map<string, SensorDto>((sensors ?? []).map((s) => [s.id, s]));
    return {
      byId,
      sensorName: (id: string) => byId.get(id)?.name,
      sensorUnit: (id: string) => byId.get(id)?.unit ?? '',
    };
  }, [sensors]);
}

/** Everything `summarizeCondition` needs, bound to the current language. */
export function useSummaryContext() {
  const { t, i18n } = useTranslation('profiles');
  const lookup = useSensorLookup();
  return useMemo(
    () => ({
      t: t as unknown as Translate,
      locale: i18n.resolvedLanguage ?? i18n.language,
      sensorName: lookup.sensorName,
      sensorUnit: lookup.sensorUnit,
    }),
    [t, i18n.resolvedLanguage, i18n.language, lookup],
  );
}
