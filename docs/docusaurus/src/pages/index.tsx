import type {ReactNode} from 'react';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';

import DocsSection from '@site/src/components/landing/DocsSection';
import FeatureCards from '@site/src/components/landing/FeatureCards';
import HeroSection from '@site/src/components/landing/HeroSection';
import ScrollytellingStage from '@site/src/components/landing/ScrollytellingStage';

export default function Home(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  return (
    <Layout
          title={`${siteConfig.title} — Review code. Not just diffs.`}
      description={siteConfig.tagline}
      wrapperClassName="landing-page">
      <HeroSection />
      <main>
        <ScrollytellingStage />
        <FeatureCards />
        <DocsSection />
      </main>
    </Layout>
  );
}
