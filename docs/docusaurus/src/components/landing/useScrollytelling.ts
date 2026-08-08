import {useLayoutEffect, type RefObject} from 'react';
import gsap from 'gsap';
import {ScrollTrigger} from 'gsap/ScrollTrigger';

import {
  easePremium,
  easeSoft,
  isMobileLayout,
  peelHiddenClip,
  peelRevealedClip,
} from './motion';
import {storyBeats} from './storyboard';
import styles from './landing.module.css';

gsap.registerPlugin(ScrollTrigger);

/** Minimum gap between stage and viewport edges */
const EDGE_MARGIN = 24;
/** Navbar + breathing room above the stage */
const STAGE_INSET = 64;
/** Extra gap between stage right edge and copy column */
const COPY_GUTTER = 40;

export type ScrollytellingElements = {
  root: RefObject<HTMLElement | null>;
  pin: RefObject<HTMLElement | null>;
  appStage: RefObject<HTMLElement | null>;
  copyRoot: RefObject<HTMLElement | null>;
  layers: Map<string, HTMLElement>;
  images: string[];
};

function copyKey(beat: {id: string; copyId?: string}): string {
  return beat.copyId ?? beat.id;
}

/** Fallback when copy column is not measurable yet */
function copyReserveFallback(vw: number): number {
  const copyW = Math.min(280, vw * 0.22);
  const rightInset = Math.min(56, Math.max(24, vw * 0.04));
  return copyW + rightInset + COPY_GUTTER;
}

/** Space reserved on the right for copy + gutter (prefer live DOM). */
function measureCopyReserve(copyRoot: HTMLElement): number {
  const rect = copyRoot.getBoundingClientRect();
  const cs = getComputedStyle(copyRoot);
  const rightInset = Number.parseFloat(cs.right) || 0;
  const width = rect.width || Math.min(280, window.innerWidth * 0.22);
  if (width > 0) {
    return width + rightInset + COPY_GUTTER;
  }
  return copyReserveFallback(window.innerWidth);
}

/** Left-biased x/scale that keeps left ≥ EDGE_MARGIN and right clear of copy. */
function measureShift(
  stageWidth: number,
  copyRoot: HTMLElement,
): {x: number; scale: number} {
  const vw = window.innerWidth;
  const reserve = measureCopyReserve(copyRoot);
  const available = vw - EDGE_MARGIN - reserve;
  const scale = Math.min(0.98, Math.max(0.5, available / stageWidth));
  const scaledW = stageWidth * scale;
  // Pin left edge at EDGE_MARGIN (stage is layout-centered at vw/2)
  const x = EDGE_MARGIN + scaledW / 2 - vw / 2;
  return {x, scale};
}

export function useScrollytelling(
  enabled: boolean,
  elements: ScrollytellingElements,
): void {
  useLayoutEffect(() => {
    if (!enabled) {
      return undefined;
    }

    const root = elements.root.current;
    const pin = elements.pin.current;
    const appStage = elements.appStage.current;
    const copyRoot = elements.copyRoot.current;
    if (!root || !pin || !appStage || !copyRoot) {
      return undefined;
    }

    const {layers, images} = elements;
    const mobile = isMobileLayout();
    const totalWeight = storyBeats.reduce((sum, b) => sum + b.scrollWeight, 0);
    const pinDistance = () =>
      Math.round(
        window.innerHeight * totalWeight * (mobile ? 0.75 : 1.05),
      );

    // Stacked layers: locked in frame; peel via clip-path (bottom → top)
    images.forEach((src, index) => {
      const el = layers.get(src);
      if (!el) {
        return;
      }
      if (index === 0) {
        gsap.set(el, {
          opacity: 1,
          yPercent: 0,
          clipPath: peelRevealedClip,
          zIndex: 1,
        });
      } else {
        gsap.set(el, {
          opacity: 1,
          yPercent: 0,
          clipPath: peelHiddenClip,
          zIndex: index + 1,
        });
      }
    });

    const measureSafeY = () => {
      const pinH = pin.clientHeight;
      const stageH = appStage.offsetHeight;
      const centered = Math.round((pinH - stageH) / 2);
      return Math.max(STAGE_INSET, centered);
    };

    gsap.set(appStage, {
      y: 0,
      x: 0,
      scale: mobile ? 1 : 0.82,
      transformOrigin: 'top center',
    });

    const panels = Array.from(
      copyRoot.querySelectorAll<HTMLElement>(`.${styles.copyPanel}`),
    );
    gsap.set(panels, {
      opacity: 0,
      y: 20,
      visibility: 'hidden',
      pointerEvents: 'none',
    });
    panels.forEach((panel) => {
      gsap.set(panel.querySelectorAll('li'), {opacity: 0, y: 10});
    });

    const syncCopyTop = () => {
      if (mobile) {
        return;
      }
      gsap.set(copyRoot, {top: `${measureSafeY() + 48}px`});
    };
    syncCopyTop();

    const tl = gsap.timeline({
      defaults: {ease: easePremium},
      scrollTrigger: {
        trigger: root,
        start: 'top top',
        end: () => `+=${pinDistance()}`,
        pin,
        scrub: mobile ? 0.55 : 0.8,
        anticipatePin: 1,
        invalidateOnRefresh: true,
        pinSpacing: true,
      },
    });

    let visibleImage = storyBeats[0]!.image;
    let activePanel: HTMLElement | null = null;
    let activeCopyKey: string | null = null;
    let stageSafeY = measureSafeY();
    const shifted = measureShift(appStage.offsetWidth, copyRoot);
    let layerZ = 1;

    const revealBullets = (
      panel: HTMLElement,
      at: string,
      segment: number,
      startFrac: number,
    ) => {
      const items = Array.from(panel.querySelectorAll('li'));
      gsap.set(items, {opacity: 0, y: 10});
      const windowLen = segment * 0.45;
      const step = items.length ? windowLen / items.length : 0;
      items.forEach((li, i) => {
        tl.to(
          li,
          {
            opacity: 1,
            y: 0,
            duration: Math.max(step * 0.8, 0.2),
            ease: easeSoft,
          },
          `${at}+=${segment * startFrac + i * step}`,
        );
      });
    };

    /**
     * Copy sequence when the panel changes:
     * 1) fade out old at beat start
     * 2) (caller starts wipe ~same time / slightly after)
     * 3) fade in new after wipe has progressed
     */
    const showPanel = (
      beat: (typeof storyBeats)[number],
      at: string,
      segment: number,
      imageChanged: boolean,
    ) => {
      const hasHeading = Boolean(beat.heading);
      const key = copyKey(beat);
      const panel = panels.find((p) => p.dataset.copy === key) ?? null;

      // Same copy panel: keep text visible through image-only beats
      if (hasHeading && panel && activePanel && activeCopyKey === key) {
        return;
      }

      // Clear old copy immediately so it leaves before / as wipe starts
      if (activePanel) {
        tl.to(
          activePanel,
          {
            opacity: 0,
            y: -10,
            visibility: 'hidden',
            pointerEvents: 'none',
            duration: segment * 0.1,
            ease: easeSoft,
          },
          at,
        );
        activePanel = null;
        activeCopyKey = null;
      }

      if (!hasHeading || !panel) {
        return;
      }

      // New copy after wipe has mostly progressed (or sooner if same image)
      const inDelay = imageChanged ? 0.6 : 0.22;
      const copyAt = `${at}+=${segment * inDelay}`;

      tl.to(
        panel,
        {
          opacity: 1,
          y: 0,
          visibility: 'visible',
          pointerEvents: 'auto',
          duration: segment * 0.18,
          ease: easeSoft,
        },
        copyAt,
      );

      revealBullets(panel, at, segment, inDelay + 0.1);
      activePanel = panel;
      activeCopyKey = key;
    };

    storyBeats.forEach((beat, beatIndex) => {
      const segment = beat.scrollWeight;
      const label = beat.id;
      tl.addLabel(label);

      if (beatIndex === 0) {
        if (mobile) {
          // Mobile: stay stacked at top — no desktop camera move
          tl.to({}, {duration: segment}, label);
          return;
        }

        // Rise from peek into safe inset, zoom with top origin (top never leaves viewport)
        stageSafeY = measureSafeY();
        tl.to(
          appStage,
          {
            y: stageSafeY,
            scale: 1,
            duration: segment * 0.85,
            ease: easePremium,
          },
          label,
        );
        tl.to(
          copyRoot,
          {
            top: stageSafeY + 48,
            duration: segment * 0.85,
            ease: easePremium,
          },
          label,
        );
        tl.to({}, {duration: segment * 0.15}, `${label}+=${segment * 0.85}`);
        return;
      }

      let imageChanged = false;
      const wipeStart = `${label}+=${segment * 0.05}`;

      if (beat.image !== visibleImage) {
        const layer = layers.get(beat.image);
        const prevLayer = layers.get(visibleImage);
        if (layer) {
          imageChanged = true;
          layerZ += 1;
          const wipeDur = Math.max(segment * 0.78, 0.6);
          const wipeOffset = segment * 0.05;
          if (beat.transition === 'none') {
            tl.set(
              layer,
              {
                opacity: 1,
                yPercent: 0,
                clipPath: peelRevealedClip,
                zIndex: layerZ,
              },
              wipeStart,
            );
            if (prevLayer && prevLayer !== layer) {
              tl.set(prevLayer, {clipPath: peelHiddenClip}, wipeStart);
            }
          } else {
            // Peel: bottom of new replaces bottom of old; line rises with scroll.
            // Keep previous fully revealed underneath until peel finishes.
            tl.set(layer, {opacity: 1, yPercent: 0, zIndex: layerZ}, wipeStart);
            tl.fromTo(
              layer,
              {clipPath: peelHiddenClip},
              {
                clipPath: peelRevealedClip,
                duration: wipeDur,
                ease: 'none',
              },
              wipeStart,
            );
            if (prevLayer && prevLayer !== layer) {
              tl.set(
                prevLayer,
                {clipPath: peelHiddenClip},
                `${label}+=${wipeOffset + wipeDur}`,
              );
            }
          }
          visibleImage = beat.image;
        }
      }

      // Desktop only: measured left shift — never clip left edge
      if (!mobile) {
        const pose = beat.shiftLeft
          ? shifted
          : {x: 0, scale: 1};
        tl.to(
          appStage,
          {
            x: pose.x,
            scale: pose.scale,
            duration: segment * 0.22,
            ease: easePremium,
          },
          label,
        );
      }

      showPanel(beat, label, segment, imageChanged);

      tl.to({}, {duration: segment * 0.12}, `${label}+=${segment * 0.88}`);
    });

    const onResize = () => ScrollTrigger.refresh();
    window.addEventListener('resize', onResize);

    return () => {
      window.removeEventListener('resize', onResize);
      tl.scrollTrigger?.kill();
      tl.kill();
    };
    // elements is a stable mutable bag; enabled / images drive rebuilds
  }, [enabled, elements, elements.images]);
}
