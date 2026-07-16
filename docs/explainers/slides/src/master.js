// master.js — assemble every explainer section into ONE presentation.
const T = require("./kse_theme.js");
const p = T.newDeck("KieshStockExchange — Product Explainers");
p.subject = "How the KieshStockExchange product works — the complete explainer";

// reading order: Big Idea → Architecture → Bot → Engine → Data → API → Host → Client
const SECTIONS = [
  "./build_00_bigidea.js",
  "./build_01.js",
  "./build_02_bot.js",
  "./build_03_engine.js",
  "./build_04_data.js",
  "./build_05_api.js",
  "./build_06_host.js",
  "./build_07_client.js",
];
SECTIONS.forEach(m => require(m)(T, p));

const out = "C:/Users/kjden/source/repos/Kieshdh/KieshStockExchange/docs/explainers/slides/KieshStockExchange_Explainers.pptx";
p.writeFile({ fileName: out }).then(() => console.log("wrote", out));
