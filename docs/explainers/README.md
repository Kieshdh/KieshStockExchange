# Explainers — the KieshStockExchange product documentation suite

Companion references that together explain the whole system. **Start with [`ARCHITECTURE.md`](ARCHITECTURE.md)** — the system map + reading order.

| # | Doc | Covers |
|---|-----|--------|
| 1 | [ARCHITECTURE.md](ARCHITECTURE.md) | System map: the projects, one request lifecycle, the shared seam, the glossary. **Start here.** |
| 2 | [ENGINE_MECHANICS.md](ENGINE_MECHANICS.md) | The market engine: OrderEntry → Execution → Matching → Settlement, reservations + the CK=0 conservation proof. |
| 3 | [DATA_LAYER.md](DATA_LAYER.md) | Persistence: EF schema vs runtime Dapper (`PgDBService`), the DB schema/ERD, transactions. |
| 4 | [API_REFERENCE.md](API_REFERENCE.md) | The REST controllers + the SignalR `MarketHub` — the client↔server wire protocol. |
| 5 | [CLIENT_STRUCTURE.md](CLIENT_STRUCTURE.md) | The MAUI client: MVVM/DI/Shell nav, the hub client, chart + F&G pane, order ticket, auth. |

Also part of the reading order, but relocated to **[`../reference/`](../reference/)** because they are settings/config
references as much as explainers:
- [**BOT_MECHANICS.md**](../reference/BOT_MECHANICS.md) — the ~20k-bot fleet (tick loop, per-bot decision, sentiment/mood, §1 market-realism target scorecard + the `Bots:*` config-key index).
- [**SERVER_HOST_AND_OPS.md**](../reference/SERVER_HOST_AND_OPS.md) — `Program.cs` composition + hosted services + Docker/prod deployment + the config-layering / env-override reference.

Slide decks (`.pptx`) generated from these live in [`slides/`](slides/).
