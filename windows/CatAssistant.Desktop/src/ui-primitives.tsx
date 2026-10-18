import React, {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useLayoutEffect,
  useRef,
  useState
} from 'react';
import { scrollThumbMetrics } from './ui-behavior';

export interface AnchorRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

export function rectOf(element: Element): AnchorRect {
  const rect = element.getBoundingClientRect();
  return { left: rect.left, top: rect.top, width: rect.width, height: rect.height };
}

export function anchoredTransformOrigin(anchor: AnchorRect, surface: AnchorRect): string {
  const x = anchor.left + anchor.width / 2 - surface.left;
  const y = anchor.top + anchor.height / 2 - surface.top;
  return `${x}px ${y}px`;
}

interface ScrollAreaProps {
  className?: string;
  viewportClassName?: string;
  children: React.ReactNode;
  onScroll?: React.UIEventHandler<HTMLDivElement>;
}

export const ScrollArea = forwardRef<HTMLDivElement, ScrollAreaProps>(function ScrollArea(
  { className = '', viewportClassName = '', children, onScroll },
  forwardedRef
) {
  const viewport = useRef<HTMLDivElement>(null);
  const rail = useRef<HTMLDivElement>(null);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const [active, setActive] = useState(false);
  const [thumb, setThumb] = useState({ visible: false, size: 0, offset: 0 });

  useImperativeHandle(forwardedRef, () => viewport.current!, []);

  const update = () => {
    const view = viewport.current;
    const track = rail.current;
    if (!view || !track) return;
    const next = scrollThumbMetrics(view.scrollHeight, view.clientHeight, view.scrollTop, track.clientHeight);
    setThumb(current => current.visible === next.visible && current.size === next.size && current.offset === next.offset ? current : next);
  };

  const reveal = () => {
    setActive(true);
    clearTimeout(hideTimer.current);
    hideTimer.current = setTimeout(() => setActive(false), 820);
  };

  useLayoutEffect(() => update(), []);
  useEffect(() => {
    const view = viewport.current;
    if (!view) return;
    const observer = new ResizeObserver(update);
    const mutation = new MutationObserver(update);
    observer.observe(view);
    if (view.firstElementChild) observer.observe(view.firstElementChild);
    mutation.observe(view, { childList: true, subtree: true, characterData: true });
    return () => {
      observer.disconnect();
      mutation.disconnect();
      clearTimeout(hideTimer.current);
    };
  }, []);

  const startDrag = (event: React.PointerEvent<HTMLButtonElement>) => {
    const view = viewport.current;
    const track = rail.current;
    if (!view || !track) return;
    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    const startY = event.clientY;
    const startTop = view.scrollTop;
    const travel = Math.max(1, track.clientHeight - thumb.size);
    const scrollRange = Math.max(0, view.scrollHeight - view.clientHeight);
    const move = (moveEvent: PointerEvent) => {
      view.scrollTop = startTop + (moveEvent.clientY - startY) * scrollRange / travel;
      update();
    };
    const finish = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      reveal();
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish, { once: true });
  };

  return <div className={`scroll-area ${active ? 'scroll-active' : ''} ${className}`}
    onPointerEnter={() => { update(); if (thumb.visible) setActive(true); }}
    onPointerLeave={() => reveal()}>
    <div ref={viewport} className={`scroll-viewport ${viewportClassName}`}
      onScroll={event => { update(); reveal(); onScroll?.(event); }}>
      {children}
    </div>
    <div ref={rail} className={`scroll-rail ${thumb.visible ? 'has-overflow' : ''}`} aria-hidden="true">
      <button tabIndex={-1} onPointerDown={startDrag}
        style={{ height: `${thumb.size}px`, transform: `translateY(${thumb.offset}px)` }}/>
    </div>
  </div>;
});

interface AnchoredSurfaceProps {
  open: boolean;
  anchor?: AnchorRect;
  titleId: string;
  className?: string;
  onClose(): void;
  children: React.ReactNode;
}

export function AnchoredSurface({ open, anchor, titleId, className = '', onClose, children }: AnchoredSurfaceProps) {
  const [rendered, setRendered] = useState(open);
  const [visible, setVisible] = useState(false);
  const panel = useRef<HTMLElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);
  const retainedAnchor = useRef<AnchorRect | undefined>(anchor);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const [origin, setOrigin] = useState('50% 50%');

  useEffect(() => {
    if (anchor) retainedAnchor.current = anchor;
    clearTimeout(closeTimer.current);
    if (open) {
      setRendered(true);
      return;
    }
    setVisible(false);
    closeTimer.current = setTimeout(() => setRendered(false), 280);
    return () => clearTimeout(closeTimer.current);
  }, [open, anchor]);

  useLayoutEffect(() => {
    if (!rendered || !open || !panel.current) return;
    const box = panel.current.getBoundingClientRect();
    const source = retainedAnchor.current;
    if (source) {
      setOrigin(anchoredTransformOrigin(source, {
        left: box.left,
        top: box.top,
        width: box.width,
        height: box.height
      }));
    } else {
      setOrigin('50% 50%');
    }
    let secondFrame = 0;
    const firstFrame = requestAnimationFrame(() => {
      secondFrame = requestAnimationFrame(() => setVisible(true));
    });
    return () => { cancelAnimationFrame(firstFrame); cancelAnimationFrame(secondFrame); };
  }, [rendered, open, anchor?.left, anchor?.top, anchor?.width, anchor?.height]);

  useEffect(() => {
    if (!rendered || !open) return;
    previousFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const app = document.querySelector<HTMLElement>('.app-frame');
    app?.setAttribute('inert', '');
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      app?.removeAttribute('inert');
      previousFocus.current?.focus({ preventScroll: true });
    };
  }, [rendered, open]);

  useEffect(() => {
    if (!visible || !open) return;
    const frame = requestAnimationFrame(() => panel.current?.querySelector<HTMLElement>('[data-autofocus], input, button')?.focus({ preventScroll: true }));
    return () => cancelAnimationFrame(frame);
  }, [visible, open]);

  if (!rendered) return null;
  return <div className={`anchor-backdrop ${visible ? 'is-open' : 'is-closing'} ${className}`}
    aria-hidden={!visible} inert={!visible}
    onPointerDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <section ref={panel} className="anchor-surface" role="dialog" aria-modal="true" aria-labelledby={titleId}
      style={{ '--modal-origin': origin } as React.CSSProperties}>
      {children}
    </section>
  </div>;
}
