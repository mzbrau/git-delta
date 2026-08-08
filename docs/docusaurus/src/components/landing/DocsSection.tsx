import type {ReactNode} from 'react';
import Link from '@docusaurus/Link';
import Heading from '@theme/Heading';

import {docsLinks} from './storyboard';
import styles from './landing.module.css';

export default function DocsSection(): ReactNode {
  return (
    <section className={styles.docs} aria-labelledby="landing-docs">
      <div className={styles.sectionInner}>
        <Heading as="h2" id="landing-docs" className={styles.sectionTitle}>
          Documentation
        </Heading>
        <p className={styles.sectionLead}>
          Dive into install guides, workflows, and reference material.
        </p>
        <div className={styles.docsGrid}>
          {docsLinks.map((item) => (
            <div key={item.to} className={styles.docsItem}>
              <Heading as="h3" className={styles.docsTitle}>
                {item.title}
              </Heading>
              <p className={styles.docsBody}>{item.body}</p>
              <Link to={item.to}>{item.label}</Link>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
