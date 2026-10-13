import type { NeedleGeometry } from '@/api/types';

interface NeedleDiagramProps {
  geometry: NeedleGeometry;
  /** Needle position 0..1 (also highlights that mark on the scale). */
  fraction?: number;
  /** "full" shows the pivot below the display; "compact" crops to the scale. */
  variant?: 'full' | 'compact';
  label: string;
  maxWidth?: number;
}

const toRadians = (degrees: number) => (degrees * Math.PI) / 180;

/**
 * Sketch of the 200×144 e-paper face with the scale arc and the needle, drawn from the same
 * geometry the face renderer uses. It makes "pivot" and "start/end angle" tangible.
 */
export function NeedleDiagram({ geometry, fraction = 0.5, variant = 'full', label, maxWidth = 300 }: NeedleDiagramProps) {
  const { pivotX, pivotY, startAngle, endAngle } = geometry;
  const angleAt = (f: number) => startAngle + (endAngle - startAngle) * f;
  const point = (f: number, radius: number) => {
    const a = toRadians(angleAt(f));
    return { x: pivotX + radius * Math.sin(a), y: pivotY - radius * Math.cos(a) };
  };
  const line = (f: number, from: number, to: number) => {
    const a = point(f, from);
    const b = point(f, to);
    return { x1: a.x, y1: a.y, x2: b.x, y2: b.y };
  };

  const full = variant === 'full';
  const arcStart = point(0, 150);
  const arcEnd = point(1, 150);
  const ticks = [0, 0.25, 0.5, 0.75, 1];
  const needle = full ? line(fraction, 0, 168) : line(fraction, 96, 168);
  const sweep = Math.abs(endAngle - startAngle) > 180 ? 1 : 0;
  const primary = 'var(--mantine-primary-color-filled)';

  return (
    <svg
      role="img"
      aria-label={label}
      viewBox={full ? '-30 -10 260 255' : '-12 50 224 104'}
      style={{ width: '100%', maxWidth, height: 'auto', display: 'block', color: 'var(--mantine-color-dimmed)' }}
    >
      <rect
        x={0}
        y={0}
        width={200}
        height={144}
        rx={6}
        style={{ fill: 'light-dark(#fff, var(--mantine-color-dark-8))' }}
        stroke="currentColor"
        strokeOpacity={0.5}
      />
      {full && (
        <>
          <line {...line(0, 0, 175)} stroke="currentColor" strokeOpacity={0.45} strokeDasharray="3 4" />
          <line {...line(1, 0, 175)} stroke="currentColor" strokeOpacity={0.45} strokeDasharray="3 4" />
        </>
      )}
      <path
        d={`M ${arcStart.x} ${arcStart.y} A 150 150 0 ${sweep} 1 ${arcEnd.x} ${arcEnd.y}`}
        fill="none"
        stroke="currentColor"
        strokeOpacity={0.7}
        strokeWidth={1.2}
      />
      {ticks.map((f) => {
        const highlighted = Math.abs(f - fraction) < 1e-6;
        return (
          <line
            key={f}
            {...line(f, highlighted ? 142 : 146, highlighted ? 162 : 156)}
            stroke={highlighted ? primary : 'currentColor'}
            strokeOpacity={highlighted ? 1 : 0.8}
            strokeWidth={highlighted ? 3 : 1.5}
            strokeLinecap="round"
          />
        );
      })}
      {[0, 0.5, 1].map((f) => {
        const p = point(f, 130);
        return (
          <text key={f} x={p.x} y={p.y + 3} fontSize={9} textAnchor="middle" fill="currentColor" fillOpacity={0.85} fontFamily="var(--mantine-font-family-monospace)">
            {Math.round(f * 100)}
          </text>
        );
      })}
      <line {...needle} stroke={primary} strokeWidth={2.4} strokeLinecap="round" />
      {full && <circle cx={pivotX} cy={pivotY} r={5} fill={primary} />}
    </svg>
  );
}
