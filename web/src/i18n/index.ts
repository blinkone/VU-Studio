import i18n from 'i18next';
import LanguageDetector from 'i18next-browser-languagedetector';
import { initReactI18next } from 'react-i18next';

// Each feature owns one namespace file per language: locales/{en,it}/<namespace>.json.
// Adding a namespace = adding the two JSON files; they are picked up automatically.
const modules = import.meta.glob('./locales/*/*.json', { eager: true, import: 'default' }) as Record<string, Record<string, unknown>>;

const resources: Record<string, Record<string, Record<string, unknown>>> = {};
for (const [path, content] of Object.entries(modules)) {
  const [, lang, ns] = /\.\/locales\/(\w+)\/([\w-]+)\.json$/.exec(path) ?? [];
  if (!lang || !ns) continue;
  resources[lang] ??= {};
  resources[lang][ns] = content;
}

void i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources,
    fallbackLng: 'en',
    supportedLngs: ['en', 'it'],
    nonExplicitSupportedLngs: true,
    defaultNS: 'common',
    ns: Object.keys(resources.en ?? { common: {} }),
    interpolation: { escapeValue: false },
    detection: { order: ['localStorage', 'navigator'], caches: ['localStorage'], lookupLocalStorage: 'vustudio.lang' },
  });

/** Picks the right language from a LocalizedText coming from the service. */
export function localized(text: { en: string; it?: string } | undefined, lang = i18n.resolvedLanguage): string {
  if (!text) return '';
  return lang?.startsWith('it') && text.it ? text.it : text.en;
}

export default i18n;
