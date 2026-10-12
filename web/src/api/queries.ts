import { useQuery } from '@tanstack/react-query';
import { api } from './client';
import type {
  AssetInfo,
  AuthDto,
  FontInfo,
  IconInfo,
  InfoDto,
  ProvidersDto,
  SensorDto,
  ThemeInfo,
} from './types';

// Server data that changes rarely. Live data (engine state, sensor values) comes from the
// WebSocket store in live.ts instead.

export const queryKeys = {
  info: ['info'] as const,
  auth: ['auth'] as const,
  config: ['config'] as const,
  sensors: ['sensors'] as const,
  providers: ['providers'] as const,
  themes: ['catalog', 'themes'] as const,
  fonts: ['catalog', 'fonts'] as const,
  icons: ['catalog', 'icons'] as const,
  assets: ['assets'] as const,
  units: (unit: string) => ['units', unit] as const,
};

export const useInfo = () => useQuery({ queryKey: queryKeys.info, queryFn: () => api.get<InfoDto>('/api/info'), staleTime: Infinity });

export const useAuth = () => useQuery({ queryKey: queryKeys.auth, queryFn: () => api.get<AuthDto>('/api/auth') });

/** Sensor catalog with the values at fetch time; refetched when the catalog changes (see live.ts). */
export const useSensors = () =>
  useQuery({ queryKey: queryKeys.sensors, queryFn: () => api.get<SensorDto[]>('/api/sensors'), staleTime: 30_000 });

export const useProviders = () =>
  useQuery({ queryKey: queryKeys.providers, queryFn: () => api.get<ProvidersDto>('/api/providers'), refetchInterval: 5_000 });

export const useThemes = () =>
  useQuery({ queryKey: queryKeys.themes, queryFn: () => api.get<ThemeInfo[]>('/api/catalog/themes'), staleTime: Infinity });

export const useFonts = () =>
  useQuery({ queryKey: queryKeys.fonts, queryFn: () => api.get<FontInfo[]>('/api/catalog/fonts'), staleTime: Infinity });

export const useIcons = () =>
  useQuery({ queryKey: queryKeys.icons, queryFn: () => api.get<IconInfo[]>('/api/catalog/icons'), staleTime: Infinity });

export const useAssets = () => useQuery({ queryKey: queryKeys.assets, queryFn: () => api.get<AssetInfo[]>('/api/assets') });

/** Display units compatible with a canonical unit (e.g. "B/s" → B/s, KB/s, …, Mbit/s). */
export const useUnits = (unit: string | undefined) =>
  useQuery({
    queryKey: queryKeys.units(unit ?? ''),
    queryFn: () => api.get<string[]>(`/api/units/${encodeURIComponent(unit ?? '')}`),
    enabled: unit !== undefined,
    staleTime: Infinity,
  });
