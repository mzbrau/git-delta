import {useEffect, useRef, type ReactNode} from 'react';
import Heading from '@theme/Heading';
import gsap from 'gsap';
import {ScrollTrigger} from 'gsap/ScrollTrigger';

import {easeSoft, prefersReducedMotion} from './motion';
import {featureCards} from './storyboard';
import styles from './landing.module.css';

gsap.registerPlugin(ScrollTrigger);

export default function FeatureCards(): ReactNode {
  const gridRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const grid = gridRef.current;
    if (!grid || prefersReducedMotion()) {
      return undefined;
    }

    const cards = grid.querySelectorAll(`.${styles.featureCard}`);
    gsap.set(cards, {opacity: 1, y: 0});
    const anim = gsap.fromTo(
      cards,
      {opacity: 0, y: 24},
      {
        opacity: 1,
        y: 0,
        duration: 0.65,
        ease: easeSoft,
        stagger: 0.05,
        immediateRender: false,
        scrollTrigger: {
          trigger: grid,
          start: 'top 75%',
          once: true,
        },
      },
    );

    return () => {
      anim.scrollTrigger?.kill();
      anim.kill();
    };
  }, []);

  return (
    <section className={styles.features} aria-labelledby="landing-features">
      <div className={styles.sectionInner}>
        <Heading as="h2" id="landing-features" className={styles.sectionTitle}>
          Built for serious review
        </Heading>
        <p className={styles.sectionLead}>
          A focused toolkit for understanding changes — locally and on GitHub.
        </p>
        <div ref={gridRef} className={styles.featureGrid}>
          {featureCards.map((card) => (
            <article key={card.title} className={styles.featureCard}>
              <Heading as="h3" className={styles.featureTitle}>
                {card.title}
              </Heading>
              <p className={styles.featureBody}>{card.body}</p>
            </article>
          ))}
        </div>
      </div>
    </section>
  );
}
