# Slide generator

The explainer decks (`../NN_*.pptx`) are generated from these scripts with [pptxgenjs](https://gitbrent.github.io/PptxGenJS/) (the official Anthropic `pptx` skill's library).

- `kse_theme.js` — the shared design system + slide builders (title / map / content[flow·mono·stat] / statement / closing) + the pipeline-strip + journey-map motif. Per-deck files are pure data.
- `build_NN_<deck>.js` — one per deck; imports the theme, defines the slides as data, writes the `.pptx`.

## Regenerate
```bash
npm install pptxgenjs
node build_01_arch.js   # writes ../01_ARCHITECTURE.pptx
```
Design lives in one place — edit `kse_theme.js` and re-run to restyle every deck.
