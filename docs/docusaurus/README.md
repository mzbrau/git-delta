# GIT DELTA documentation site

This folder is the **Docusaurus** project for the user guide.

- Markdown content lives in the parent [`docs/`](../) folder (sibling of this directory).
- Configure and build from here:

```bash
cd docs/docusaurus
npm install
npm start          # syncs landing PNGs, then local preview
npm run build      # syncs landing PNGs, then production build → ./build
```

Landing-page screenshots are authored in [`docs/img/landing/`](../img/landing/) and copied into `static/img/landing/` by `npm run sync:landing` (`prestart` / `prebuild`). Guide screenshots live in [`docs/img/`](../img/).

GitHub Pages deploys via [`.github/workflows/docs.yml`](../../.github/workflows/docs.yml) on pushes to `main` that change `docs/**`.
