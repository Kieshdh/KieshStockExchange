# Explainers — the KieshStockExchange product documentation suite

Seven companion references that together explain the whole system. **Start with [`ARCHITECTURE.md`](ARCHITECTURE.md)** — the system map + reading order.

| # | Doc | Covers |
|---|-----|--------|
| 1 | [ARCHITECTURE.md](ARCHITECTURE.md) | System map: the projects, one request lifecycle, the shared seam, the glossary. **Start here.** |
| 2 | [BOT_MECHANICS.md](BOT_MECHANICS.md) | The ~20k-bot fleet: the tick loop, per-bot decision, sentiment/mood/Fear-&-Greed, market-realism targets. |
| 3 | [ENGINE_MECHANICS.md](ENGINE_MECHANICS.md) | The market engine: OrderEntry → Execution → Matching → Settlement, reservations + the CK=0 conservation proof. |
| 4 | [DATA_LAYER.md](DATA_LAYER.md) | Persistence: EF schema vs runtime Dapper (`PgDBService`), the DB schema/ERD, transactions. |
| 5 | [API_REFERENCE.md](API_REFERENCE.md) | The REST controllers + the SignalR `MarketHub` — the client↔server wire protocol. |
| 6 | [SERVER_HOST_AND_OPS.md](SERVER_HOST_AND_OPS.md) | `Program.cs` composition + hosted services + Docker/prod deployment + config layering. |
| 7 | [CLIENT_STRUCTURE.md](CLIENT_STRUCTURE.md) | The MAUI client: MVVM/DI/Shell nav, the hub client, chart + F&G pane, order ticket, auth. |

Slide decks (`.pptx`) generated from these live in [`slides/`](slides/).
