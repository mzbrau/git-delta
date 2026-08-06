import type {ReactNode} from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';

import styles from './index.module.css';

function HomepageHeader() {
  const {siteConfig} = useDocusaurusContext();
  return (
    <header className={clsx('hero hero--primary', styles.heroBanner)}>
      <div className="container">
        <Heading as="h1" className="hero__title">
          {siteConfig.title}
        </Heading>
        <p className="hero__subtitle">{siteConfig.tagline}</p>
        <div className={styles.buttons}>
          <Link
            className="button button--secondary button--lg"
            to="/docs/intro">
            Read the docs
          </Link>
          <Link
            className="button button--outline button--secondary button--lg"
            to="/docs/getting-started/requirements"
            style={{marginLeft: '0.75rem'}}>
            Getting started
          </Link>
        </div>
      </div>
    </header>
  );
}

export default function Home(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  return (
    <Layout
      title={siteConfig.title}
      description={siteConfig.tagline}>
      <HomepageHeader />
      <main>
        <div className="container margin-vert--lg">
          <div className="row">
            <div className="col col--4">
              <Heading as="h3">Getting started</Heading>
              <p>Install on Windows or macOS, then open your first repository.</p>
              <Link to="/docs/getting-started/requirements">Requirements →</Link>
            </div>
            <div className="col col--4">
              <Heading as="h3">Guides</Heading>
              <p>Working copy, diffs, history, pull requests, and AI-assisted review.</p>
              <Link to="/docs/tour/main-window">App tour →</Link>
            </div>
            <div className="col col--4">
              <Heading as="h3">Reference</Heading>
              <p>Settings, shortcuts, performance, and troubleshooting.</p>
              <Link to="/docs/reference/settings">Settings →</Link>
            </div>
          </div>
        </div>
      </main>
    </Layout>
  );
}
