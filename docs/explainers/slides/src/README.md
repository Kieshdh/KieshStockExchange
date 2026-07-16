# Slide generator — one combined presentation

`../KieshStockExchange_Explainers.pptx` (77 slides, 8 sections) is generated from these scripts with [pptxgenjs](https://gitbrent.github.io/PptxGenJS/) (the official Anthropic `pptx` skill's library).

- `kse_theme.js` — the shared design system + slide builders (title / map / content[flow·mono·stat] / statement / closing) + the pipeline-strip + journey-map motif. **Edit here to restyle every slide.**
- `build_NN_<deck>.js` — one section each; exports `function(T, p)` that adds its slides. Pure data.
- `master.js` — assembles all sections (Big Idea → Architecture → Bot → Engine → Data → API → Host → Client) into the one `.pptx`.

## Regenerate
```bash
npm install pptxgenjs
node master.js      # writes ../KieshStockExchange_Explainers.pptx
```
