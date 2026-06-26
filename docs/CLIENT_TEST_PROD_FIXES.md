# Client UI Test — PROD round (2026-06-26)

Kiesh is running the client against **PROD** (`https://kieshstockexchange.duckdns.org`, BaseUrl reverted) and
working the A–F plan (see `docs/WAVE10_CLIENT_UI_TEST.md`). **Workflow:** Kiesh logs observations here as he tests
→ I add each with a root-cause hypothesis + proposed fix (researched as it lands) → **DO NOT IMPLEMENT until Kiesh
says go** (his explicit instruction; realism is priority 1, testing is priority 2).

**Context that shapes fixes:**
- **Prod runs `master`** (old config) — the branch's server-side + realism changes are NOT deployed. The branch's
  **client-side** UI fixes (I1–I8) WILL show if Kiesh runs a branch build of the client; server-dependent behavior
  reflects master. Note per-observation whether it's a client-only or server-dependent issue.
- Fix conventions (repo CLAUDE.md): identify the LAYER first (View / ViewModel / Model / Service / Helper / Data);
  minimal targeted edits; MVVM-clean (bindings/commands/observable props over code-behind); shared XAML styles;
  preserve model invariants + conservation (CK=0); multi-table writes via `RunInTransactionAsync`; don't touch `/Tools`.

---

## 📋 Observations — PROD round (A–F)
_(empty — I'll fill each as Kiesh reports it)_

| # | Section | Symptom (Kiesh) | Root-cause hypothesis | Fix (layer) | Client/Server | Status |
|---|---------|-----------------|-----------------------|-------------|---------------|--------|
| | | | | | | |

---

## 🔧 Fixing-round runbook (when Kiesh says "go")
1. Group by file/layer; order low-risk → high-risk.
2. Implement each (minimal, MVVM-clean, correct layer).
3. Build (server if touched + client `net9.0-windows`) + `dotnet test` (gate: green).
4. Commit (focused per-fix or sensibly grouped) + push.
5. Update each row's Status → DONE (commit ref). Hand back for re-test.
