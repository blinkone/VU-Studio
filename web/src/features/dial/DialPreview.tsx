import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useDialState } from '@/api/hooks';
import type { DialStateSnapshot, NeedleGeometry } from '@/api/types';
import { useConfigDraft } from '@/state/configDraft';
import { isDark, paperColor } from './color';
import { faceImageUrl, formatReading, readingParts } from './format';
import classes from './DialPreview.module.css';

export type DialPreviewSize = 'sm' | 'md' | 'lg';

export interface DialPreviewProps {
  slot: number;
  /** Maximum width of the dial: sm ≈ 240 px, md ≈ 340 px, lg ≈ 450 px. It shrinks to fit its container. */
  size?: DialPreviewSize;
  /** Live state of the slot; read from the live store when omitted. */
  state?: DialStateSnapshot;
  /** Face image to show instead of the one on the dial (e.g. a preview of an unsaved page). */
  faceUrl?: string;
  /** Needle position 0..100 to show instead of the live one. */
  needle?: number;
  /** Backlight color "#RRGGBB" to show instead of the live one; null = light off. */
  color?: string | null;
  /** Title shown under the dial instead of the live one. */
  title?: string;
  /** Reading shown under the dial instead of the live value (already formatted); null = no value. */
  valueText?: string | null;
  /** Shows the slot, name, status, title, value and page dots around the dial. */
  withInfo?: boolean;
  /** With `withInfo`, also shows the slot/name/status row above the dial. */
  withHeader?: boolean;
  /** Soft colored light spilling behind the dial. */
  ambient?: boolean;
  /** Makes the dial a link (e.g. to its editor). */
  to?: string;
  /** Accessible label of the link. */
  linkLabel?: string;
  /** Extra content at the right of the header row (before the status). */
  headerExtra?: ReactNode;
}

const DEFAULT_GEOMETRY: NeedleGeometry = { pivotX: 99.5, pivotY: 225.5, startAngle: -40, endAngle: 40 };

/** Needle length in face pixels: the tip sweeps just inside the printed scale (radius ≈ 152). */
const NEEDLE_LENGTH = 150;

/**
 * A realistic VU1 dial: dark housing, the e-paper face lit by the backlight color, and an analog
 * needle that eases between the ~10 Hz live updates like a damped meter movement.
 */
export function DialPreview({
  slot,
  size = 'md',
  state: explicitState,
  faceUrl,
  needle,
  color,
  title,
  valueText,
  withInfo = true,
  withHeader = true,
  ambient = false,
  to,
  linkLabel,
  headerExtra,
}: DialPreviewProps) {
  const { t, i18n } = useTranslation('dashboard');
  const liveState = useDialState(slot);
  const state = explicitState ?? liveState;
  const geometry = useConfigDraft((s) => s.base?.hardware.geometry) ?? DEFAULT_GEOMETRY;

  const lightHex = color !== undefined ? color : state && !isDark(state.light) ? state.previewColor : null;
  const lit = !!lightHex && lightHex.toUpperCase() !== '#000000';
  const stageStyle = {
    '--dial-paper': paperColor(lit ? lightHex : null),
    '--dial-glow': lit ? lightHex! : 'rgba(0, 0, 0, 0)',
  } as CSSProperties;

  const src = faceUrl ?? (state ? faceImageUrl(slot, state.faceVersion) : undefined);
  const needleValue = needle ?? state?.needle ?? 0;
  const online = state?.online ?? false;
  const alarm = state?.alarmActive ?? false;

  const shownTitle = title ?? state?.title ?? t('common:slot', { slot });
  const reading = readingParts(state?.value, state?.unit, state?.valueText, i18n.language);
  const unassigned = state !== undefined && !state.assigned;
  const pageCount = state?.pageCount ?? 0;
  const pageIndex = state?.pageIndex ?? 0;

  const ariaLabel = t('dial.aria', {
    slot,
    title: shownTitle,
    value: valueText ?? (valueText === null ? t('dial.noValue') : unassigned ? t('dial.unassigned') : formatReading(state?.value, state?.unit, state?.valueText, i18n.language)),
    light: lit ? lightHex : t('dial.lightOff'),
  });

  const housing = (
    <div className={classes.housing}>
      <div className={classes.window}>
        <div className={classes.paper} />
        <FaceImage src={src} />
        <div className={classes.vignette} />
        <Needle value={needleValue} geometry={geometry} />
        <div className={classes.glass} />
      </div>
      <div className={classes.chin} aria-hidden>
        <span className={classes.engraving}>{String(slot).padStart(2, '0')}</span>
        <span className={classes.led} data-online={online || undefined} data-alarm={alarm || undefined} />
      </div>
    </div>
  );

  return (
    <div className={classes.root} data-size={size}>
      {withInfo && withHeader && (
        <div className={classes.head}>
          <div className={classes.slotLabel}>
            <span className={classes.slotNumber}>{t('common:slot', { slot })}</span>
            {state?.name && <span className={classes.slotName}>{state.name}</span>}
          </div>
          {headerExtra}
          {alarm ? (
            <span className={classes.status} data-alarm>
              <span className={classes.statusDot} />
              {t('dial.alarm')}
            </span>
          ) : (
            <span className={classes.status} data-online={online || undefined}>
              <span className={classes.statusDot} />
              {online ? t('dial.online') : t('dial.offline')}
            </span>
          )}
        </div>
      )}

      <div className={classes.stage} style={stageStyle}>
        {ambient && lit && <div className={classes.ambient} aria-hidden />}
        <div className={classes.dial}>
          {to ? (
            <Link to={to} className={classes.link} aria-label={linkLabel ?? ariaLabel}>
              {housing}
            </Link>
          ) : (
            <div role="img" aria-label={ariaLabel}>
              {housing}
            </div>
          )}
          {alarm && <div className={classes.alarm} aria-hidden />}
        </div>
      </div>

      {withInfo && (
        <div className={classes.foot}>
          <div className={classes.reading}>
            <span className={classes.title} title={shownTitle}>
              {shownTitle}
            </span>
            {valueText === null ? (
              <span className={classes.value} data-muted>
                {t('dial.noValue')}
              </span>
            ) : valueText !== undefined ? (
              <span className={classes.value}>{valueText}</span>
            ) : unassigned ? (
              <span className={classes.value} data-muted>
                {t('dial.unassigned')}
              </span>
            ) : reading.empty ? (
              <span className={classes.value} data-muted>
                {t('dial.noValue')}
              </span>
            ) : (
              <span className={classes.value} title={formatReading(state?.value, state?.unit, state?.valueText, i18n.language)}>
                {reading.value}
                {reading.unit && <span className={classes.unit}>{reading.unit}</span>}
              </span>
            )}
          </div>
          <div className={classes.pages}>
            {pageCount > 1 && (
              <>
                <span className={classes.dots} role="img" aria-label={t('dial.page', { index: pageIndex + 1, count: pageCount })}>
                  {Array.from({ length: Math.min(pageCount, 12) }, (_, i) => (
                    <span key={i} className={classes.dot} data-active={i === pageIndex || undefined} />
                  ))}
                </span>
                <span className={classes.pageName}>
                  {state?.pageName || t('dial.page', { index: pageIndex + 1, count: pageCount })}
                </span>
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- face

/** The e-paper face. New images are decoded off-screen first, then cross-faded in. */
function FaceImage({ src }: { src: string | undefined }) {
  const [current, setCurrent] = useState<string | null>(null);
  const [previous, setPrevious] = useState<string | null>(null);
  const shown = useRef<string | null>(null);

  useEffect(() => {
    if (!src || src === shown.current) return;
    let cancelled = false;
    const img = new Image();
    img.src = src;
    img.decode().then(
      () => {
        if (cancelled) return;
        setPrevious(shown.current);
        shown.current = src;
        setCurrent(src);
      },
      // Not rendered yet, or no renderer: keep the last good face (or the bare lit paper).
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [src]);

  return (
    <>
      {previous && (
        <img
          key={`out:${previous}`}
          src={previous}
          alt=""
          draggable={false}
          className={`${classes.face} ${classes.faceOut}`}
          onAnimationEnd={() => setPrevious(null)}
        />
      )}
      {current && <img key={`in:${current}`} src={current} alt="" draggable={false} className={`${classes.face} ${classes.faceIn}`} />}
    </>
  );
}

// ---------------------------------------------------------------- needle

const prefersReducedMotion = () => typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

// Damped spring (ω ≈ 20 rad/s, ζ = 0.8): ~1.5 % overshoot, like a real VU movement.
const OMEGA = 20;
const ZETA = 0.8;

function Needle({ value, geometry }: { value: number; geometry: NeedleGeometry }) {
  const rawId = useId();
  const shadowId = `needle-shadow-${rawId.replace(/[^a-zA-Z0-9_-]/g, '')}`;
  const group = useRef<SVGGElement>(null);
  const geo = useRef(geometry);
  geo.current = geometry;
  const sim = useRef({ x: clamp(value), v: 0, target: clamp(value), raf: 0, last: 0 });

  const apply = useCallback(() => {
    const g = geo.current;
    const angle = g.startAngle + ((g.endAngle - g.startAngle) * sim.current.x) / 100;
    group.current?.setAttribute('transform', `rotate(${angle.toFixed(3)} ${g.pivotX} ${g.pivotY})`);
  }, []);

  useLayoutEffect(apply, [apply, geometry]);

  useEffect(() => {
    const s = sim.current;
    s.target = clamp(value);
    if (prefersReducedMotion()) {
      s.x = s.target;
      s.v = 0;
      apply();
      return;
    }
    if (s.raf) return; // the running loop picks up the new target
    s.last = performance.now();
    const tick = (now: number) => {
      let dt = Math.min(0.05, Math.max(0, (now - s.last) / 1000));
      s.last = now;
      while (dt > 0) {
        const h = Math.min(dt, 0.004);
        const a = OMEGA * OMEGA * (s.target - s.x) - 2 * ZETA * OMEGA * s.v;
        s.v += a * h;
        s.x += s.v * h;
        dt -= h;
      }
      if (Math.abs(s.target - s.x) < 0.01 && Math.abs(s.v) < 0.05) {
        s.x = s.target;
        s.v = 0;
        s.raf = 0;
        apply();
        return;
      }
      apply();
      s.raf = requestAnimationFrame(tick);
    };
    s.raf = requestAnimationFrame(tick);
  }, [value, apply]);

  useEffect(
    () => () => {
      cancelAnimationFrame(sim.current.raf);
      sim.current.raf = 0;
    },
    [],
  );

  const { pivotX: px, pivotY: py } = geometry;
  const tipStart = NEEDLE_LENGTH - 30;
  // Tapered shaft from below the window up to the tip; half-width shrinks from 1.5 to 0.45 px.
  const half = (r: number) => 1.5 - ((1.5 - 0.45) * (r - 40)) / (NEEDLE_LENGTH - 40);
  const shaft = `${px - half(40)},${py - 40} ${px - half(tipStart)},${py - tipStart} ${px + half(tipStart)},${py - tipStart} ${px + half(40)},${py - 40}`;
  const tip = `${px - half(tipStart)},${py - tipStart} ${px - 0.45},${py - NEEDLE_LENGTH} ${px + 0.45},${py - NEEDLE_LENGTH} ${px + half(tipStart)},${py - tipStart}`;

  return (
    <svg className={classes.needle} viewBox="0 0 200 144" preserveAspectRatio="none" aria-hidden focusable="false">
      <defs>
        <filter id={shadowId} x="-200%" y="-20%" width="500%" height="140%">
          <feDropShadow dx="1.4" dy="1.8" stdDeviation="0.9" floodColor="#000" floodOpacity="0.38" />
        </filter>
      </defs>
      <g ref={group} filter={`url(#${shadowId})`}>
        <polygon points={shaft} fill="#141518" />
        <polygon points={tip} fill="#ff5a1f" />
      </g>
    </svg>
  );
}

function clamp(v: number) {
  return Number.isFinite(v) ? Math.max(0, Math.min(100, v)) : 0;
}
