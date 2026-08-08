import type {ReactNode} from 'react';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Heading from '@theme/Heading';

import styles from './landing.module.css';

const quickLinks = [
  {to: '/docs/tour/main-window', label: 'App tour'},
  {to: '/docs/local/diff-viewer', label: 'Diff viewer'},
  {to: '/docs/review/pull-request-review', label: 'Pull requests'},
  {to: '/docs/review/ai-assist', label: 'AI assist'},
] as const;

export default function HeroSection(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  const logo = useBaseUrl('img/logo.png');

  return (
    <header className={styles.hero}>
      <div className={styles.heroContent}>
        <div className={styles.brandRow}>
          <img
            className={styles.heroLogo}
            src={logo}
            alt=""
            width={128}
            height={128}
          />
          <p className={styles.brandName}>{siteConfig.title}</p>
        </div>
        <Heading as="h1" className={styles.heroTitle}>
          Review code. Not just diffs.
        </Heading>
        <p className={styles.heroTagline}>{siteConfig.tagline}</p>
        <p className={styles.heroBlurb}>
          An AI-powered Git client that makes diffs, pull requests, and local
          reviews easier to understand — without leaving your machine.
        </p>
        <div className={styles.heroCtas}>
          <Link className={styles.ctaPrimary} to="/docs/intro">
            Read the docs
          </Link>
          <Link
            className={styles.ctaSecondary}
            href="https://github.com/mzbrau/git-delta/releases/latest">
            Download
          </Link>
        </div>
        <ul className={styles.quickLinks}>
          {quickLinks.map((link) => (
            <li key={link.to}>
              <Link to={link.to}>{link.label}</Link>
            </li>
          ))}
        </ul>
      </div>
      {/* Peek is provided by ScrollytellingStage for a continuous handoff */}
      <div className={styles.heroPeekSpacer} aria-hidden />
    </header>
  );
}
