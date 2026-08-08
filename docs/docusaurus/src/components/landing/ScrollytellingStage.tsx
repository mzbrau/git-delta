import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import useBaseUrl from '@docusaurus/useBaseUrl';
import Heading from '@theme/Heading';

import {prefersReducedMotion} from './motion';
import {
  storyBeats,
  uniqueCopyBeats,
  uniqueStoryImages,
  type StoryBeat,
} from './storyboard';
import {useScrollytelling} from './useScrollytelling';
import styles from './landing.module.css';

function withBase(path: string, baseUrl: string): string {
  const base = baseUrl.endsWith('/') ? baseUrl : `${baseUrl}/`;
  return `${base}${path.replace(/^\//, '')}`;
}

function copyKey(beat: StoryBeat): string {
  return beat.copyId ?? beat.id;
}

function BeatCopy({beat}: {beat: StoryBeat}): ReactNode {
  if (!beat.heading) {
    return null;
  }
  return (
    <div className={styles.copyPanel} data-copy={copyKey(beat)}>
      <Heading as="h2" className={styles.copyHeading}>
        {beat.heading}
      </Heading>
      <ul className={styles.bulletList}>
        {beat.bullets.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

function StaticStory(): ReactNode {
  const baseUrl = useBaseUrl('/');
  const chapters = useMemo(() => {
    // Collapse consecutive beats that share copy into one chapter for reduced motion
    const result: StoryBeat[] = [];
    for (const beat of storyBeats) {
      const prev = result[result.length - 1];
      if (
        prev &&
        beat.heading &&
        prev.heading === beat.heading &&
        (beat.copyId ?? beat.id) === (prev.copyId ?? prev.id) &&
        beat.bullets.join('\0') === prev.bullets.join('\0')
      ) {
        continue;
      }
      if (!beat.heading && result.length === 0) {
        // Keep enter image as first chapter without copy
        result.push(beat);
        continue;
      }
      if (!beat.heading) {
        continue;
      }
      result.push(beat);
    }
    return result;
  }, []);

  return (
    <section className={styles.staticStory} aria-label="Product tour">
      {chapters.map((beat) => (
        <article key={beat.id} className={styles.staticChapter}>
          <div className={styles.staticFrame}>
            <img
              src={withBase(beat.image, baseUrl)}
              alt={beat.heading ?? 'GIT DELTA application'}
              loading="lazy"
              decoding="async"
            />
          </div>
          {beat.heading ? (
            <div className={styles.staticCopy}>
              <Heading as="h2">{beat.heading}</Heading>
              <ul>
                {beat.bullets.map((item) => (
                  <li key={item}>{item}</li>
                ))}
              </ul>
            </div>
          ) : null}
        </article>
      ))}
    </section>
  );
}

export default function ScrollytellingStage(): ReactNode {
  const [reduced, setReduced] = useState(false);
  const rootRef = useRef<HTMLElement>(null);
  const pinRef = useRef<HTMLDivElement>(null);
  const appStageRef = useRef<HTMLDivElement>(null);
  const copyRef = useRef<HTMLDivElement>(null);
  const layerRefs = useRef(new Map<string, HTMLElement>());
  const baseUrl = useBaseUrl('/');
  const images = useMemo(() => uniqueStoryImages(), []);
  const copyBeats = useMemo(() => uniqueCopyBeats(), []);

  useEffect(() => {
    setReduced(prefersReducedMotion());
    const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
    const onChange = () => setReduced(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);

  const setLayerRef = useCallback((src: string, el: HTMLImageElement | null) => {
    if (el) {
      layerRefs.current.set(src, el);
    } else {
      layerRefs.current.delete(src);
    }
  }, []);

  // Stable bag for the GSAP hook (refs + image list). Layers map is mutated via callback refs.
  const elements = useRef({
    root: rootRef,
    pin: pinRef,
    appStage: appStageRef,
    copyRoot: copyRef,
    layers: layerRefs.current,
    images,
  });
  elements.current.images = images;

  useScrollytelling(!reduced, elements.current);

  if (reduced) {
    return <StaticStory />;
  }

  return (
    <section
      ref={rootRef}
      className={styles.story}
      aria-label="Interactive product tour">
      <div ref={pinRef} className={styles.storyPin}>
        <div className={styles.storyInner}>
          <div ref={appStageRef} className={styles.appStage}>
            <div className={styles.appFrame}>
              {images.map((src) => (
                <img
                  key={src}
                  ref={(el) => setLayerRef(src, el)}
                  className={styles.appLayer}
                  src={withBase(src, baseUrl)}
                  alt=""
                  draggable={false}
                  decoding="async"
                />
              ))}
            </div>
          </div>
          <div ref={copyRef} className={styles.copyColumn}>
            {copyBeats.map((beat) => (
              <BeatCopy key={copyKey(beat)} beat={beat} />
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}
