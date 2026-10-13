import { IconApps, IconDeviceDesktop, IconHome2, IconNetwork, IconTools, IconWorld } from '@tabler/icons-react';
import type { Icon } from '@tabler/icons-react';
import type { ProviderGroup } from '@/api/types';

/** Order of the sections in the "Add a source" gallery. */
export const GROUP_ORDER: readonly ProviderGroup[] = ['thisPc', 'software', 'network', 'homeAutomation', 'online', 'utility'];

export const GROUP_ICONS: Record<ProviderGroup, Icon> = {
  thisPc: IconDeviceDesktop,
  software: IconApps,
  network: IconNetwork,
  homeAutomation: IconHome2,
  online: IconWorld,
  utility: IconTools,
};

export type Platform = 'any' | 'windows' | 'linux' | 'macOS';

/** `descriptor.platforms` is the service's flags enum as text: "any", "windows", "windows, linux"… */
export function parsePlatforms(platforms: string | undefined): Platform[] {
  const tokens = (platforms ?? 'any')
    .split(/[\s,|]+/)
    .map((token) => token.trim().toLowerCase())
    .filter(Boolean);
  if (tokens.length === 0 || tokens.includes('any')) return ['any'];

  const result: Platform[] = [];
  for (const token of tokens) {
    const platform: Platform | null =
      token === 'windows' ? 'windows' : token === 'linux' ? 'linux' : token === 'macos' ? 'macOS' : null;
    if (platform && !result.includes(platform)) result.push(platform);
  }
  return result.length > 0 ? result : ['any'];
}
