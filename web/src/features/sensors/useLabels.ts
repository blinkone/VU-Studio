import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useProviders } from '@/api/queries';
import { localized } from '@/i18n';
import { useConfigDraft } from '@/state/configDraft';
import type { Labels } from './model';

/** Human names for categories, sensor kinds and provider instances (their label, else their type's name). */
export function useLabels(): Labels {
  const { t, i18n } = useTranslation('sensors');
  const providers = useProviders();
  const instances = useConfigDraft((s) => s.base?.providers);
  const types = providers.data?.types;

  return useMemo(() => {
    const typeNames = new Map((types ?? []).map((type) => [type.descriptor.type, localized(type.descriptor.name, i18n.language)]));
    const byId = new Map<string, string>();
    for (const instance of instances ?? []) {
      byId.set(instance.id, instance.name?.trim() || typeNames.get(instance.type) || instance.type);
    }
    return {
      category: (category: string) => t(`categories.${category}`, { ns: 'common', defaultValue: category }),
      kind: (kind: string | undefined) => (kind ? t(`kinds.${kind}`, { defaultValue: kind }) : ''),
      provider: (providerId: string) => byId.get(providerId) ?? (['session', 'push'].includes(providerId) ? t(`providers.${providerId}`) : providerId),
    };
  }, [t, i18n.language, instances, types]);
}
