import type { TFunction } from 'i18next';
import type { DialPage, SensorDto, ThemeInfo } from '@/api/types';
import { localized } from '@/i18n';

/** Name shown for a page: its own name, else its label or sensor name, else "Page N". */
export function pageLabel(page: DialPage, index: number, sensors: SensorDto[] | undefined, t: TFunction): string {
  return (
    page.name?.trim() ||
    page.value.label?.trim() ||
    sensors?.find((s) => s.id === page.value.sensorId)?.name ||
    t('editor:pageFallback', { index: index + 1 })
  );
}

/** Localized theme name, or a tidy version of its id ("minimal/arc" → "Minimal · Arc"). */
export function themeLabel(themeId: string, themes: ThemeInfo[] | undefined, lang: string): string {
  const theme = themes?.find((th) => th.id === themeId);
  if (theme) return localized(theme.name, lang);
  return themeId
    .split('/')
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' · ');
}
