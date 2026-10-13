import { useMemo } from 'react';
import { useIcons } from '@/api/queries';

interface MdiIconProps {
  /** Icon id from the service's icon catalog (Material Design Icons subset). */
  id: string | undefined;
  size?: number;
  color?: string;
  title?: string;
}

/** Renders one of the bundled Material Design Icons (the same set the e-paper faces use). */
export function MdiIcon({ id, size = 20, color = 'currentColor', title }: MdiIconProps) {
  const { data } = useIcons();
  const path = useMemo(() => data?.find((i) => i.id === id)?.path, [data, id]);
  if (!path) return <span style={{ display: 'inline-block', width: size, height: size }} />;
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" role={title ? 'img' : undefined} aria-label={title} aria-hidden={!title}>
      <path d={path} fill={color} />
    </svg>
  );
}
