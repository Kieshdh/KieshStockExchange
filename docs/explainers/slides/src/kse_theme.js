// kse_theme.js — shared design system + slide builders for the KieshStockExchange explainer decks.
// Council-specced (Fable-5, 2026-07-16): native shapes over ASCII, assertion titles, <=4 bullets,
// speaker notes as the distillation valve, a persistent pipeline strip + journey-map motif, semantic palette.
// Per-deck files are PURE DATA (arrays of slide specs); this module renders them.
const pptxgen = require("pptxgenjs");

const W = 13.333, H = 7.5;
const C = {
  dark: "0E1420", darkPanel: "182536", darkEdge: "24344A",
  light: "F5F7FA", panel: "FFFFFF", edge: "E2E7EF",
  ink: "16202E", body: "3A4658", muted: "8A95A5", faint: "B9C2CE",
  up: "16C784", upInk: "0E9F6E",   // green: fill (up) / darker ink for green text on light
  down: "EA3943", gold: "F5B942",
  slate: "44557A", slateLite: "6279A6",   // neutral structural fills for diagram boxes
  monoInk: "D8E0EC", monoBg: "111C2B", monoEdge: "233247",
};
const FONT = "Segoe UI", MONO = "Consolas";
// The product pipeline the whole story hangs on. Each deck lights its zone(s).
const STAGES = ["CLIENT", "API", "ENTRY", "EXEC", "MATCH", "SETTLE", "DB"];

function newDeck(title) {
  const p = new pptxgen();
  p.defineLayout({ name: "W", width: W, height: H });
  p.layout = "W";
  p.author = "KieshStockExchange";
  p.title = title;
  return p;
}

// ---- shared chrome -----------------------------------------------------------
function bar(s, color) { s.addShape("rect", { x: 0, y: 0, w: 0.14, h: H, fill: { color } }); }
function chips(s, deckNum, section, color) {
  s.addShape("roundRect", { x: 0.5, y: 0.4, w: 0.56, h: 0.56, rectRadius: 0.07, fill: { color } });
  s.addText(String(deckNum).padStart(2, "0"), { x: 0.5, y: 0.4, w: 0.56, h: 0.56, align: "center", valign: "middle", fontSize: 18, bold: true, color: C.dark, fontFace: FONT });
  s.addText(section.toUpperCase(), { x: 1.2, y: 0.4, w: 9.3, h: 0.56, valign: "middle", fontSize: 11, bold: true, color: C.muted, charSpacing: 3, fontFace: FONT, margin: 0 });
  s.addText(`${deckNum} / 7`, { x: W - 1.7, y: 0.4, w: 1.2, h: 0.56, valign: "middle", align: "right", fontSize: 11, bold: true, color: C.faint, fontFace: FONT, margin: 0 });
}
// footer pipeline strip; active = array of stage names lit gold
function pipeStrip(s, active) {
  const y = 6.92, h = 0.4, x0 = 0.5, tot = W - 1.0, gap = 0.08;
  const cw = (tot - gap * (STAGES.length - 1)) / STAGES.length;
  STAGES.forEach((st, i) => {
    const on = active.includes(st);
    const x = x0 + i * (cw + gap);
    s.addShape("roundRect", { x, y, w: cw, h, rectRadius: 0.04, fill: { color: on ? C.gold : C.edge }, line: { color: on ? C.gold : C.edge, width: 1 } });
    s.addText(st, { x, y, w: cw, h, align: "center", valign: "middle", fontSize: 9, bold: on, color: on ? C.dark : C.muted, fontFace: FONT, margin: 0 });
  });
}
function titleAssertion(s, text, color) {
  s.addText(text, { x: 0.5, y: 1.0, w: 12.3, h: 0.9, fontSize: 25, bold: true, color: C.ink, fontFace: FONT, valign: "top", margin: 0, lineSpacingMultiple: 0.98 });
  s.addShape("line", { x: 0.52, y: 1.82, w: 1.0, h: 0, line: { color, width: 3 } });
}
function bullets(s, x, y, w, h, title, items, color) {
  if (title) s.addText(title, { x, y, w, h: 0.4, fontSize: 14, bold: true, color, fontFace: FONT, margin: 0, charSpacing: 1 });
  const rt = items.slice(0, 4).map((b, i) => ({
    text: typeof b === "string" ? b : b.t,
    options: { bullet: { code: "2022", indent: 16 }, color: C.body, fontSize: 14, bold: !!(b.bold), breakLine: true, paraSpaceBefore: i ? 10 : 0, lineSpacingMultiple: 1.04 },
  }));
  s.addText(rt, { x, y: y + (title ? 0.5 : 0), w, h: h - (title ? 0.5 : 0), valign: "top", fontFace: FONT, margin: 0 });
}
function notes(s, txt) { if (txt) s.addNotes(txt); }

// ---- native-shape visuals ----------------------------------------------------
// vertical flow of rounded-rect nodes joined by down-arrows. nodes:[{t, sub?, color?}]
function flow(s, x, y, w, nodes, accent) {
  const n = nodes.length, arrow = 0.28;
  const nh = Math.min(0.74, (( 4.55) - arrow * (n - 1)) / n);
  let cy = y;
  nodes.forEach((nd, i) => {
    const fill = nd.color || C.slate;
    s.addShape("roundRect", { x, y: cy, w, h: nh, rectRadius: 0.06, fill: { color: fill }, line: { color: fill, width: 1 } });
    const label = nd.sub
      ? [{ text: nd.t, options: { bold: true, color: "FFFFFF", fontSize: 13, breakLine: true } }, { text: nd.sub, options: { color: "DCE5F2", fontSize: 10.5 } }]
      : [{ text: nd.t, options: { bold: true, color: "FFFFFF", fontSize: 13.5 } }];
    s.addText(label, { x: x + 0.2, y: cy, w: w - 0.4, h: nh, valign: "middle", fontFace: FONT, margin: 0, align: "center" });
    if (i < n - 1) s.addShape("line", { x: x + w / 2, y: cy + nh, w: 0, h: arrow, line: { color: C.muted, width: 2, endArrowType: "triangle" } });
    cy += nh + arrow;
  });
}
// journey map: the 7-stage product pipeline as a labeled row, one zone lit. Used as slide-2 of each deck.
function journeyMap(s, x, y, w, h, zone) {
  const gap = 0.14, cw = (w - gap * (STAGES.length - 1)) / STAGES.length;
  STAGES.forEach((st, i) => {
    const on = (Array.isArray(zone) ? zone : [zone]).includes(st);
    const cx = x + i * (cw + gap);
    s.addShape("roundRect", { x: cx, y, w: cw, h, rectRadius: 0.06, fill: { color: on ? C.gold : C.slate }, line: { color: on ? C.gold : C.slate, width: 1 } });
    s.addText(st, { x: cx, y, w: cw, h, align: "center", valign: "middle", fontSize: 11, bold: true, color: on ? C.dark : "DCE5F2", fontFace: FONT, margin: 0 });
    if (i < STAGES.length - 1) s.addShape("line", { x: cx + cw, y: y + h / 2, w: gap, h: 0, line: { color: C.faint, width: 1.5, endArrowType: "triangle" } });
  });
  s.addText("bots feed the same book ↑", { x, y: y + h + 0.1, w, h: 0.3, align: "center", fontSize: 10, italic: true, color: C.muted, fontFace: FONT, margin: 0 });
}
// mono/ledger artifact panel (authentic code/JSON/log/ledger only). lines:[{t, color?}] or string
function monoPanel(s, x, y, w, h, caption, lines, size) {
  s.addShape("roundRect", { x, y, w, h, rectRadius: 0.06, fill: { color: C.monoBg }, line: { color: C.monoEdge, width: 1 } });
  if (caption) s.addText(caption, { x: x + 0.24, y: y + 0.12, w: w - 0.48, h: 0.3, fontSize: 10, bold: true, color: C.gold, charSpacing: 2, fontFace: MONO, margin: 0 });
  const arr = (Array.isArray(lines) ? lines : String(lines).split("\n")).map(l => {
    const o = typeof l === "string" ? { t: l } : l;
    return { text: o.t, options: { color: o.color || C.monoInk, fontSize: size || 12.5, breakLine: true, fontFace: MONO } };
  });
  s.addText(arr, { x: x + 0.28, y: y + (caption ? 0.5 : 0.26), w: w - 0.56, h: h - (caption ? 0.72 : 0.5), valign: "top", fontFace: MONO, lineSpacingMultiple: 1.14, margin: 0 });
}
// stat tiles: cards:[{v, k, d?}]
function statTiles(s, x, y, w, h, cards, accent) {
  const n = cards.length, gap = 0.24, ch = (h - gap * (n - 1)) / n;
  cards.forEach((c, i) => {
    const yy = y + i * (ch + gap);
    s.addShape("roundRect", { x, y: yy, w, h: ch, rectRadius: 0.06, fill: { color: C.panel }, line: { color: C.edge, width: 1 } });
    s.addShape("rect", { x, y: yy, w: 0.1, h: ch, fill: { color: accent } });
    s.addText(c.v, { x: x + 0.28, y: yy, w: 2.5, h: ch, valign: "middle", fontSize: 25, bold: true, color: C.ink, fontFace: FONT, margin: 0 });
    s.addText([{ text: c.k, options: { bold: true, color: C.ink, fontSize: 13, breakLine: true } }, { text: c.d || "", options: { color: C.muted, fontSize: 10.5 } }],
      { x: x + 2.75, y: yy, w: w - 2.95, h: ch, valign: "middle", fontFace: FONT, margin: 0 });
  });
}

// ---- SLIDE BUILDERS (the 7-type vocabulary) ----------------------------------
const LX = 0.5, LW = 5.55, VY = 2.0, VH = 4.55, RX = 6.35, RW = 6.45;

function titleSlide(p, o) {                     // dark bookend
  const s = p.addSlide(); s.background = { color: C.dark };
  s.addShape("rect", { x: 0, y: 0, w: 0.2, h: H, fill: { color: o.color || C.up } });
  if (o.kicker) s.addText(o.kicker.toUpperCase(), { x: 0.9, y: 1.45, w: 11.4, h: 0.4, fontSize: 13, bold: true, color: o.color || C.up, charSpacing: 4, fontFace: FONT });
  s.addText(o.title, { x: 0.85, y: 1.9, w: 11.6, h: 2.0, fontSize: 44, bold: true, color: "FFFFFF", fontFace: FONT, lineSpacingMultiple: 1.0, valign: "top" });
  if (o.subtitle) s.addText(o.subtitle, { x: 0.9, y: 4.05, w: 11.1, h: 1.5, fontSize: 19, color: "C7D0DC", fontFace: FONT, lineSpacingMultiple: 1.16 });
  // conservation badge
  s.addShape("roundRect", { x: 0.9, y: 6.35, w: 1.55, h: 0.5, rectRadius: 0.25, fill: { color: C.darkPanel }, line: { color: C.up, width: 1 } });
  s.addText([{ text: "Σ Δ = 0", options: { color: C.up, bold: true, fontSize: 14, fontFace: MONO } }], { x: 0.9, y: 6.35, w: 1.55, h: 0.5, align: "center", valign: "middle", margin: 0 });
  s.addText(o.footer || "KieshStockExchange · product explainers", { x: 2.65, y: 6.35, w: 9.7, h: 0.5, valign: "middle", fontSize: 11, color: C.muted, fontFace: FONT, margin: 0 });
  notes(s, o.notes);
  return s;
}
function mapSlide(p, o) {                        // slide-2 "you are here"
  const s = p.addSlide(); s.background = { color: C.light };
  bar(s, C.gold); chips(s, o.deckNum, o.section || "You are here", C.gold);
  titleAssertion(s, o.title || "Where this deck sits in one order's journey", C.gold);
  journeyMap(s, 0.6, 2.5, 12.1, 0.95, o.zone);
  bullets(s, 0.9, 4.4, 11.5, 1.9, o.afterTitle || "After this deck you'll understand", o.after, C.upInk);
  pipeStrip(s, Array.isArray(o.zone) ? o.zone : [o.zone]); notes(s, o.notes);
  return s;
}
function contentSlide(p, o) {                    // CONCEPT / FLOW / TRACE / STAT share this frame
  const s = p.addSlide(); s.background = { color: C.light };
  const A = o.accent || C.up;
  bar(s, A); chips(s, o.deckNum, o.section, A); titleAssertion(s, o.title, A);
  const v = o.visual;
  if (v.kind === "flow") flow(s, LX, VY, LW, v.nodes, A);
  else if (v.kind === "mono") monoPanel(s, LX, VY, LW, VH, v.caption, v.lines, v.size);
  else if (v.kind === "stat") statTiles(s, LX, VY, LW, VH, v.cards, A);
  else if (v.kind === "map") journeyMap(s, LX, VY + 1.4, LW, 0.8, v.zone);
  bullets(s, RX, VY, RW, VH, o.right.title, o.right.bullets, A);
  if (o.foot) s.addText(o.foot, { x: 0.5, y: 6.55, w: 12.3, h: 0.3, fontSize: 10, italic: true, color: C.muted, fontFace: FONT, margin: 0 });
  pipeStrip(s, o.pipe || []); notes(s, o.notes);
  return s;
}
function statement(p, o) {                       // full-bleed dark statement (1 layout break/deck)
  const s = p.addSlide(); s.background = { color: o.bg || C.dark };
  s.addText(o.text, { x: 1.0, y: 2.4, w: 11.3, h: 2.7, fontSize: 40, bold: true, color: "FFFFFF", fontFace: FONT, valign: "middle", lineSpacingMultiple: 1.05 });
  if (o.sub) s.addText(o.sub, { x: 1.05, y: 5.1, w: 11.0, h: 0.9, fontSize: 18, color: o.color || C.gold, fontFace: FONT });
  notes(s, o.notes); return s;
}
function closingSlide(p, o) {                    // dark hand-off
  const s = p.addSlide(); s.background = { color: C.dark };
  s.addShape("rect", { x: 0, y: 0, w: 0.2, h: H, fill: { color: C.gold } });
  s.addText("TAKEAWAYS", { x: 0.9, y: 1.2, w: 11, h: 0.4, fontSize: 13, bold: true, color: C.gold, charSpacing: 4, fontFace: FONT });
  const rt = o.takeaways.slice(0, 3).map((t, i) => ({ text: t, options: { bullet: { code: "2022", indent: 18 }, color: "E6ECF4", fontSize: 20, bold: true, breakLine: true, paraSpaceBefore: i ? 16 : 0, lineSpacingMultiple: 1.05 } }));
  s.addText(rt, { x: 0.95, y: 1.85, w: 11.4, h: 3.3, valign: "top", fontFace: FONT });
  if (o.next) {
    s.addShape("roundRect", { x: 0.9, y: 5.7, w: 11.5, h: 0.85, rectRadius: 0.1, fill: { color: C.darkPanel }, line: { color: C.darkEdge, width: 1 } });
    s.addText([{ text: "NEXT   ", options: { color: C.gold, bold: true, fontSize: 14, fontFace: FONT } }, { text: o.next, options: { color: "E6ECF4", fontSize: 16, fontFace: FONT } }], { x: 1.15, y: 5.7, w: 11.0, h: 0.85, valign: "middle", margin: 0 });
  }
  notes(s, o.notes); return s;
}

module.exports = { C, FONT, MONO, W, H, STAGES, newDeck, titleSlide, mapSlide, contentSlide, statement, closingSlide };
