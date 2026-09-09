import React from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import Layout from '@theme/Layout';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import styles from './index.module.css';

const features = [
  {
    title: 'An editor, not a framework',
    body: 'Scene graph, inspector, asset browser, prefabs, tile painting and a timeline in one dockable ImGui window, in front of the live game.',
  },
  {
    title: 'C# with hot reload',
    body: 'Write Components and SceneComponents in your own project. Save a file and the editor recompiles and reloads the scene.',
  },
  {
    title: 'Ships as one binary',
    body: 'Publish with NativeAOT and trimming from inside the editor. Source-generated serialization means no runtime reflection.',
  },
  {
    title: 'Built for automation',
    body: 'A loopback gateway, the voltage CLI and an MCP server let scripts and AI agents drive the editor like a person would.',
  },
];

function Feature({title, body}) {
  return (
    <div className={clsx('col col--6', styles.feature)}>
      <div className={styles.card}>
        <h3>{title}</h3>
        <p>{body}</p>
      </div>
    </div>
  );
}

export default function Home() {
  const {siteConfig} = useDocusaurusContext();
  return (
    <Layout title="Voltage Engine" description={siteConfig.tagline}>
      <header className={clsx('hero', styles.hero)}>
        <div className="container">
          <h1 className={styles.title}>{siteConfig.title}</h1>
          <p className={styles.subtitle}>{siteConfig.tagline}</p>
          <div className={styles.buttons}>
            <Link className="button button--primary button--lg" to="/docs/intro">
              Read the docs
            </Link>
            <Link className="button button--secondary button--outline button--lg" to="/docs/getting-started/installation">
              Build the editor
            </Link>
          </div>
        </div>
      </header>
      <main>
        <section className={styles.features}>
          <div className="container">
            <div className="row">
              {features.map((props) => (
                <Feature key={props.title} {...props} />
              ))}
            </div>
          </div>
        </section>
      </main>
    </Layout>
  );
}
