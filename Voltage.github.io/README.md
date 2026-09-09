# Voltage documentation site

A [Docusaurus 3](https://docusaurus.io/) site that renders the Markdown in the repository's `docs/` folder. Edit pages there; this folder only holds the site configuration, theme and landing page.

```bash
npm install
npm run start     # live-reloading dev server
npm run build     # static site in build/, fails on broken links
```

Requires Node 20 or newer. `.github/workflows/docs.yml` builds and publishes the site to GitHub Pages on every push to `main` that touches `docs/` or this folder.
