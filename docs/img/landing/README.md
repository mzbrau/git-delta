# Landing page screenshots

Fixed-size application screenshots for the Docusaurus homepage scrollytelling tour.

**Capture size:** ~2940 × 1782 px (app window only, no OS chrome). All seven shots should match that window size.

| Filename | Chapter |
| --- | --- |
| `01-app-overview.png` | Hero + enter |
| `02-diff-unified.png` | Diff (unified) |
| `03-diff-side-by-side.png` | Diff (side-by-side wipe) |
| `04-pr-review.png` | Pull request review |
| `05-ai-overview.png` | AI change briefing |
| `06-ai-file-summary.png` | AI file briefing |
| `07-light-theme.png` | Light theme reveal |

Source of truth is this folder. Sync into the Docusaurus static tree before `npm start` / `npm run build`:

```bash
cd docs/docusaurus
npm run sync:landing
```

The `prestart` / `prebuild` scripts run this automatically.
