import { IconPlugConnected } from '@tabler/icons-react';
import { useIcons } from '@/api/queries';
import { MdiIcon } from '@/components/MdiIcon';

interface ProviderIconProps {
  /** Icon id from the descriptor. */
  icon: string | undefined;
  size?: number;
}

/** A provider's icon from the service's catalog; a generic plug when the catalog has no such id. */
export function ProviderIcon({ icon, size = 22 }: ProviderIconProps) {
  const { data } = useIcons();
  if (data && !data.some((i) => i.id === icon)) {
    return <IconPlugConnected size={size} stroke={1.6} aria-hidden />;
  }
  return <MdiIcon id={icon} size={size} />;
}
