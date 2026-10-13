import { useIcons } from '@/api/queries';
import { MdiIcon } from '@/components/MdiIcon';
import { IconStack2 } from '@tabler/icons-react';

interface ProfileIconProps {
  icon: string | undefined;
  size?: number;
}

/** The profile's Material Design icon, or a generic stack icon when it has none (or it is unknown). */
export function ProfileIcon({ icon, size = 24 }: ProfileIconProps) {
  const { data } = useIcons();
  if (icon && data?.some((i) => i.id === icon)) return <MdiIcon id={icon} size={size} />;
  return <IconStack2 size={size} stroke={1.5} aria-hidden />;
}
