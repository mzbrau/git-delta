/** Soft premium easing — no overshoot */
export const easePremium = 'power2.inOut';
export const easeSoft = 'power1.inOut';

/** New layer fully clipped — nothing visible (peel start) */
export const peelHiddenClip = 'inset(100% 0% 0% 0%)';
/** New layer fully revealed (peel end) */
export const peelRevealedClip = 'inset(0% 0% 0% 0%)';

export function prefersReducedMotion(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export function isMobileLayout(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }
  return window.matchMedia('(max-width: 768px)').matches;
}
