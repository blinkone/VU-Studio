import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';

/**
 * Renders only the rows near the viewport of a very long list. The page itself scrolls (no inner
 * scroll box), so the list behaves like normal content on phones. Rows have known heights.
 */
export function useWindowVirtualizer(count: number, sizeOf: (index: number) => number, overscanPx = 480) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [range, setRange] = useState({ start: 0, end: Math.min(count, 24) });

  const offsets = useMemo(() => {
    const result = new Float64Array(count + 1);
    for (let i = 0; i < count; i++) result[i + 1] = result[i] + sizeOf(i);
    return result;
  }, [count, sizeOf]);
  const total = offsets[count];

  const measure = useCallback(() => {
    const element = containerRef.current;
    if (!element) return;
    const top = element.getBoundingClientRect().top;
    const viewTop = -top - overscanPx;
    const viewBottom = -top + window.innerHeight + overscanPx;

    // First row ending below the top edge, first row starting at or below the bottom edge.
    let lo = 0;
    let hi = count;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (offsets[mid + 1] > viewTop) hi = mid;
      else lo = mid + 1;
    }
    const start = lo;
    hi = count;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (offsets[mid] >= viewBottom) hi = mid;
      else lo = mid + 1;
    }
    setRange((previous) => (previous.start === start && previous.end === lo ? previous : { start, end: lo }));
  }, [count, offsets, overscanPx]);

  useLayoutEffect(measure, [measure]);

  useEffect(() => {
    let frame = 0;
    const schedule = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(measure);
    };
    // Capture: any scrolling ancestor counts, not only the window.
    window.addEventListener('scroll', schedule, { passive: true, capture: true });
    window.addEventListener('resize', schedule);
    // Content above the list may change height (toolbar wrapping) without any scroll.
    const observer = new ResizeObserver(schedule);
    observer.observe(document.documentElement);
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener('scroll', schedule, true);
      window.removeEventListener('resize', schedule);
      observer.disconnect();
    };
  }, [measure]);

  return { containerRef, total, offsets, start: Math.min(range.start, count), end: Math.min(range.end, count) };
}
