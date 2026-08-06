# GIT DELTA documentation site

This folder is the **Docusaurus** project for the user guide.

- Markdown content lives in the parent [`docs/`](../) folder (sibling of this directory).
- Configure and build from here:

```bash
cd docs/docusaurus
npm install
npm start          # local preview
npm run build      # production build → ./build
```

GitHub Pages deploys via [`.github/workflows/docs.yml`](../../.github/workflows/docs.yml) on pushes to `main` that change `docs/**`.

Screenshot capture checklist: [`static/img/placeholders/README.md`](./static/img/placeholders/README.md).
