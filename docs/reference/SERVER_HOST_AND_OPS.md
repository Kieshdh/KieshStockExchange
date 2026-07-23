# SERVER_HOST_AND_OPS.md — how the server is composed and how prod runs

Compact reference for the **process and the box**: how `KieshStockExchange.Server` wires itself up in memory (the HOST half) and how that process is built, configured, and run under Docker on the prod VM (the OPS half). Companion to `docs/explainers/ENGINE_MECHANICS.md` (what an order does once placed) and `docs/explainers/BOT_MECHANICS.md` (who places orders). This file is the layer *underneath* both — the DI graph they live in and the container that hosts it. **Consult + UPDATE this file whenever a hosted service, config gate, compose service, or the deploy flow changes** (same commit).

**System at a glance.** One ASP.NET Core process (`.NET 9`) hosts everything server-side: the HTTP API (controllers), one SignalR hub, the in-memory market engine + account caches, and a fleet of long-lived background actors (the bot loop, the stop watcher, the broadcasters, the retention prune). Persistence is **Postgres via hand-written Dapper** (`PgDBService`); EF Core exists *only* for migrations. On the prod box it runs as three Docker containers — **postgres + server + caddy** — brought up with `docker compose … up -d`. Config is layered `appsettings.json → appsettings.{Environment}.json → environment "Section__Key" overrides`, and the live experimental knobs (the Fear/Greed + co-fire flags) live in the **compose `server.environment` block**, not in any checked-in appsettings.

**Reading the references.** File/symbol references are concrete. The composition root is `KieshStockExchange.Server/Program.cs` (top-level statements, read top-to-bottom = boot order). Hosted services live in `KieshStockExchange.Server/Services/HostedServices/`. Ops files are at repo root: `docker-compose.yml`, `docker-compose.prod.yml`, `Caddyfile`, `.env.production.example`, and `KieshStockExchange.Server/Dockerfile`. **Line numbers rot with every edit above them — grep the symbol (e.g. `AddHostedService`, `Db:AutoMigrate`, `!reset`), not a line number.** Anything stated below that I inferred rather than read in source is flagged *(inferred)*.

**Map:**
- **§1 — Composition root.** `Program.cs` read as the DI graph + the strict boot sequence.
- **§2 — Config layering.** The three-tier merge and how `Section__Key` env vars map to appsettings keys.
- **§3 — Request pipeline.** JWT, CORS, forwarded headers, rate limiting, health checks.
- **§4 — Hosted services.** The long-lived actors and what each one drives.
- **§5 — Dockerfile.** The multi-stage build + non-root runtime + healthcheck.
- **§6 — Compose.** Base file (dev) + prod override, the live env-override block, the migrate one-shot.
- **§7 — Caddy + env + secrets.** TLS termination, `.env.production`, what must be overridden.
- **§8 — Deploy + rollback.** How a change reaches the box; the revert-env pattern.
- **§9 — Indexes.** Config-gate table, env→appsettings map, glossary, runbook pointers.

§1–§4 are the HOST half (the process); §5–§8 are the OPS half (the box); §9 is the lookup.

---

# HOST — the process

## 1. COMPOSITION ROOT — `Program.cs` as the DI graph

`Program.cs` is top-level statements: **read top to bottom and you are reading boot order.** It has three phases — (A) `builder.Services` registration, (B) `builder.Build()`, then (C) a strict pre-`Run()` warm-up sequence. The ordering in phase C is load-bearing and commented as such in-source.

### 1.1 Registration highlights (phase A)

The engine's shared state is registered **singleton** because it *is* the market — one order book set, one accounts cache, one order registry for the process lifetime:

| Registration | Symbol | Why singleton |
|---|---|---|
| `IDataBaseService` | `PgDBService` | Dapper + an `AsyncLocal` transaction stack; one pool |
| `IOrderRegistry` / `IAccountsCache` / `IOrderBookEngine` | in-memory engine state | shared across all users, whole-process |
| `IMatchingEngine` / `ISettlementEngine` / `IOrderExecutionService` / `IOrderEntryService` | stateless-per-call | share the caches above |
| `MarketMoodService` | Fear/Greed writer | one instance injected into both `AiTradeService` (writer) + `CandleService` (flush-time stamper) — see the `§fear-greed` comment |
| `IAiTradeService` | `AiTradeService` | the bot fleet; the hosted loop just starts/stops it |

Note three deliberate DI tricks:
- **`Lazy<IStopWatcher>`** — breaks the cycle `StopTriggerWatcher → OrderExecutionService → BracketCoordinator → IStopWatcher`. Grep `Lazy<IStopWatcher>`.
- **`StopTriggerWatcher` registered three times** — once as the concrete type, once as `IStopWatcher` (the arm/disarm surface `OrderEntryService` calls), once as `IHostedService` (the quote loop). All three resolve the **same singleton instance** (`sp.GetRequiredService<StopTriggerWatcher>`). Same pattern would apply to any actor that is both a callable service and a background loop.
- **`IBotMaintenanceQueries`** is the *same* `PgDBService` singleton re-exposed under a server-only query interface (no second instance) so the bot-maintenance path never leaks onto the shared `IDataBaseService` / client surface.

Several statics are configured **before** `builder.Build()` by reading `builder.Configuration` directly (not via DI) — `MatchSymmetryProbe`, `BotDecisionProbe`, `MidReference`, `CurrencyHelper.PriceTickExtraDecimals`, `Candle.HLMinFillSize`, `FxRateService`. These are read-once-at-startup levers; each is byte-identical to legacy behaviour at its default (grep the symbol + its `Configure`).

### 1.2 The boot sequence (phase C — order is strict)

After `builder.Build()`, before `app.Run()`, the following run **in this order** and the order is the whole point (comments in-source spell out why each must precede the next):

1. **Global exception nets** — `TaskScheduler.UnobservedTaskException` + `AppDomain.CurrentDomain.UnhandledException` both routed to Serilog, so a "silent" soak death always leaves a log line. Paired with `HostOptions.BackgroundServiceExceptionBehavior = Ignore` (a faulting `BackgroundService` must NOT take the host down).
2. **Migrate** — `if (Db:AutoMigrate)` (default **true**) → `KseDbContextFactory().CreateDbContext(args).Database.Migrate()`. This is the EF-tool-independent fallback (the runtime is Dapper; see §6.3). A migration failure is logged `Critical` and **swallowed** — a bad migration surfaces via health checks, it does not fail the boot. `Db:AutoMigrate=false` is the escape hatch for multi-instance rollouts (`Database.Migrate()` races across replicas). *Assumes single-instance deployment.*
3. **Seed** — `if (Seed:AutoOnEmptyDb)` and the DB is empty → `ExcelSeedService.SeedAllFromEmbeddedAsync()` from `Resources/Raw/AIUserData.xlsx`. **Must run before the warm-up** — the warm-up snapshots the DB into in-memory caches, so seeding later (it used to be a hosted service, which only starts at `Run()`) froze empty caches on a fresh DB. A populated DB is skipped.
4. **Stock catalogue cold-load** — `IStockService.EnsureLoadedAsync()`. Without it `OrderValidator` rejects every order as "Invalid stock ID".
5. **Bracket rehydrate** — `IBracketCoordinator.RehydrateAsync()` rebuilds the bracket-parent index from DB so brackets self-manage after a restart.
6. **Candle warm-up** — subscribe all 7 chart resolutions × every supported currency, `BackfillUpwardAsync` (aggregate 5m → 1h/4h/1d), then `PrimeRingsAsync` (last 500 candles per key into RAM). Makes chart switches serve from RAM from minute zero.

Then the middleware pipeline is wired (§3) and `app.Run()` blocks. **`ShutdownTimeout` is bumped to 60s** (`HostOptions`) so the bot loop can finish its in-flight tick + flush ring buffers on a clean stop; the longest path is `AiTradeService` dispose + the reservation-ledger CSV flush.

---

## 2. CONFIG LAYERING — three tiers, one merge

Standard ASP.NET Core `IConfiguration`, layered lowest-to-highest precedence:

1. **`appsettings.json`** — the base; every default lives here. This is the big file: the whole `Bots:*` realism config, `Retention:*`, `Db:*`, `Serilog`, `Seed`, `Users:SeedBalanceUsd`.
2. **`appsettings.{ASPNETCORE_ENVIRONMENT}.json`** — `appsettings.Production.json` (or `.Development.json`) **deep-merges** over the base: only the leaf keys present override, the rest of the tree stays. Prod's file re-asserts `Seed:AutoOnEmptyDb` (already true in base), switches the Serilog file sink to the JSON formatter, and carries a block of `Bots:*` tuning overrides (`TradeIntervalMs`, `Staggering:Slots`, `DipBuyStrength`, `OrderMaxAgeSec`, the stop-pool `StopReplaceOld`/`PruneLimitOnly`/`LeanReload` flags, `Rotator`/`BankEstimate` on) plus a `Retention` window block.
3. **Environment variables** `Section__Key` — highest precedence. **Double-underscore `__` is the config-path separator** (`:` isn't legal in an env var name on Linux). So `Bots__Mood__Enabled=true` sets `Bots:Mood:Enabled`, and `Auth__SigningKey` sets `Auth:SigningKey`. **This is the tier the live prod experiment flags use** (§6.2) — reversible, no rebuild of appsettings, no schema.

Array elements index numerically: `Cors__AllowedOrigins__0` = `Cors:AllowedOrigins[0]` (see the base compose file). Bindings read via `GetValue("Section:Key", default)` (e.g. `Db:AutoMigrate`, `Bots:AutoStart`, `Retention:Enabled`) — the default in code is what runs if the key is absent from all three tiers.

**Practical consequence:** to change a prod runtime knob you do **not** edit appsettings and rebuild — you add/change a `Section__Key` line in `docker-compose.prod.yml`'s `server.environment` and `up -d server`. To roll it back you delete the line and `up -d server` again (§8).

---

## 3. REQUEST PIPELINE — auth, CORS, limits, health

Registered in phase A, wired in phase C after the warm-up. Middleware **order matters** and is commented in-source.

- **JWT** (`Auth:*` → `JwtSettings`) — `AddJwtBearer` with issuer/audience/lifetime/signing-key validation, 30s clock skew. `Auth:SigningKey` is **mandatory**; boot throws if it's blank, and **throws in Production if it still equals the checked-in dev key** (`DevSigningKey` const) — prod must supply `Auth__SigningKey` (openssl rand -hex 32). SignalR can't carry an `Authorization` header on the WS upgrade, so a `JwtBearerEvents.OnMessageReceived` hook lifts the token from `?access_token=…` for `/hubs` paths.
- **Authorization** — a **fallback policy** requires an authenticated user on *every* endpoint unless `[AllowAnonymous]`. Only auth/login and `/healthz/*` are anonymous.
- **CORS** — origins from `Cors:AllowedOrigins` (empty in dev; prod sets `Cors__AllowedOrigins__0`). `AllowCredentials` + any header/method. Native MAUI `HttpClient` sends no `Origin` header (CORS is a browser mechanism), so the policy only bites a hypothetical browser client — `.env.production.example` states this explicitly.
- **Forwarded headers** — `UseForwardedHeaders()` runs **first** in the pipeline so downstream (rate-limit partitioning, scheme/IP) sees the real client behind Caddy. `KnownProxies`/`KnownNetworks` are **cleared** because Caddy is the only hop and sits on a non-loopback compose address.
- **Rate limiting** — fixed-window: `"orders"` policy caps order/portfolio mutations at **60/min** partitioned by authenticated user id (falling back to client IP); `"auth"` caps login at **10/min** per IP. Over-limit = immediate `429`, no queue. Reads are unlimited. `UseRateLimiter()` runs *after* auth so the partition key can read the `sub` claim.
- **Health checks** — `/healthz/live` is bare liveness (predicate `_ => false` ⇒ runs no checks, always 200 if the process is up); `/healthz/ready` additionally runs `DatabaseHealthCheck` (a trivial `GetStocksAsync` read). Both `AllowAnonymous`. The Docker `HEALTHCHECK` hits **`/healthz/live`** deliberately — a slow/contended DB must not mark the container unhealthy and trigger a restart loop.
- **Kestrel body cap** raised to **256 MB** (default 30 MB) — `TradeSettler`'s settle-group bundle at peak bot load approaches the default.
- **Static files** — `UseStaticFiles()` is placed **before** auth so the admin log-viewer page (`wwwroot/admin/logs.html`) serves as a public asset (its SSE endpoint is the auth-gated part).

---

## 4. HOSTED SERVICES — the long-lived actors

Registered via `AddHostedService` (grep it in `Program.cs`). `IHostedService.StartAsync` fires at `app.Run()`; `StopAsync` on shutdown. `BackgroundService` subclasses run a loop in `ExecuteAsync`. Most are **config-gated** and log a dormant line when off.

| Service | File | Gate | Drives |
|---|---|---|---|
| **BotLoopHostedService** | `BotLoopHostedService.cs` | `Bots:AutoStart` (true in base appsettings) | Starts/stops `AiTradeService` — the ~20k-bot trading loop. The whole market's activity. Flip + restart is the on/off. |
| **StopTriggerWatcher** | `StopTriggerWatcher.cs` | always on | Watches live quotes, promotes armed stop/trailing-stop orders when price crosses the trigger. Cold-loads armed stops from DB on start (survive restart); trailing watermarks persist throttled/batched off the quote thread. Has a §P6 per-(stock,ccy) promotion **circuit breaker** (`Bots:StopBreaker:*`) so a cascade can't fire dozens at once. |
| **MarketHubBroadcaster** | `MarketHubBroadcaster.cs` | always on | Bridges engine events → SignalR groups: `QuoteUpdated`/`CandleClosed` → `quotes:{stockId}:{currency}`, portfolio changes → `portfolio:{userId}`. Subscribes at boot, detaches on stop. |
| **OrderBookBroadcaster** | `OrderBookBroadcaster.cs` | always on | On-change order-book snapshots → the same `quotes:` group, **throttled max 1/100ms per key**, `BookVersion` hash-skips no-op flushes. Own 50ms ticker loop lazily subscribes newly-created books. |
| **TelemetryBroadcaster** | `TelemetryBroadcaster.cs` | always on | Bridges the in-memory `TelemetryBus` (operator heartbeats) → the admin-only `telemetry` SignalR group for the BotDashboard live panel. |
| **RetentionHostedService** | `RetentionHostedService.cs` | `Retention:Enabled` | `PeriodicTimer` every `Retention:IntervalMinutes`, runs one live history-prune cycle per tick (bounds the two high-churn tables + caps fine candles). A failing cycle is logged and the loop continues. On-demand admin endpoints work regardless of the flag. |
| **BotTelemetryWarmupHostedService** | `BotTelemetryWarmupHostedService.cs` | always on | One-shot: 2s after boot, warms the two slow BotDashboard queries (`BotTelemetryCache`) so the first dashboard poll doesn't pay the cold multi-GB scan. |

All SignalR pushes are **fire-and-forget** with a `ContinueWith` that logs a `Warning` on fault — a slow client never blocks the engine/log-write path. Group-name helpers: `MarketHub.GroupNameQuotes/GroupNamePortfolio/GroupNameTelemetry`. The `orders:{userId}` group is fed separately by `SignalROrderCacheService` (reuses the `IOrderCacheService.NotifyOrdersMutated` hook the engine already calls) rather than a parallel broadcaster.

---

# OPS — the box

## 5. DOCKERFILE — multi-stage, non-root

`KieshStockExchange.Server/Dockerfile`, two stages:

- **`build`** (`dotnet/sdk:9.0`) — restores just the two csprojs (`.Server` + `.Shared`), then `dotnet publish -c Release`. This stage is *also* the target of the prod `migrate` service (§6.3), because it's the only image that has the EF tooling + source.
- **`runtime`** (`dotnet/aspnet:9.0`) — creates a non-root `appuser` (uid/gid 1000), installs `curl` (the aspnet image ships neither curl nor wget, and the healthcheck needs it), copies the publish output chowned to `appuser`, pre-creates + chowns `/app/data` (ReservationLedger CSV) and `/app/logs` (Serilog volume mount). Runs as `appuser`, `EXPOSE 8080`.
- **HEALTHCHECK** — `curl -fsS http://localhost:8080/healthz/live` every 30s, `start-period=120s`. **Liveness not readiness** (see §3) — a DB blip must not restart the container.

The server listens on `:8080` inside the container (`ASPNETCORE_URLS: http://+:8080`, set in compose).

---

## 6. COMPOSE — dev base + prod override

Two files, stacked. **The override is applied on top of the base**, never alone.

### 6.1 Base — `docker-compose.yml` (dev shape)

Three services. Local dev typically runs only `docker compose up -d postgres` and runs the server from the IDE.

- **postgres** (`postgres:16-alpine`) — creds from `POSTGRES_*` env (default `kse`/`kse`/`kse-dev`), `pgdata` named volume, **publishes `5432:5432`**, `pg_isready` healthcheck.
- **server** — built from the Dockerfile, `depends_on: postgres (service_healthy)`, `ASPNETCORE_ENVIRONMENT: Production`, reads `KSE_DB_CONNECTION_STRING` / `Auth__SigningKey` / `Cors__AllowedOrigins__0` from env, `serverlogs` volume at `/app/logs`.
- **caddy** (`caddy:2-alpine`) — publishes `80`/`443`, mounts `./Caddyfile` read-only + cert/config volumes, reads `DOMAIN`.

### 6.2 Prod override — `docker-compose.prod.yml`

Applied with:
```
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production <cmd>
```
Three prod-only concerns:

1. **Un-publish Postgres.** `ports: !reset []` on postgres. Compose **concatenates** list fields across override files, so a plain `ports: []` would NOT drop the inherited `5432:5432` — the **`!reset` tag (Compose ≥ 2.24)** is required to actually remove it. On a public VM this keeps the DB off the internet; the server still reaches it over the compose network by service name (`Host=postgres` in the connection string).
2. **The live experiment env block.** `server.environment` carries the flags that are ON in prod but default-off in appsettings — reversible, no schema, no rebuild of config. As of this writing:
   - **Fear/Greed per-strategy reaction (Stage A+B, live since 2026-07-14):** `Bots__Mood__Enabled`, `__TakerCoupling`, `__ConvictionFearBid`, `__PerStrategy`, `__MMWiden` = all `true`.
   - **GlobalCoFire cross-stock correlation @0.10 (council-approved, live 2026-07-16):** `Bots__ExogShock__Enabled=true`, `__GlobalFraction=0.4`, `__MeanIntervalMinutes=0.5`, `__GlobalCoFire=true`, `__GlobalCoFireFraction=0.15`, `__GlobalCoFireNotionalFrac=0.1`, `__AnchorTracksShock=false`.
   Each maps to a `Bots:Mood:*` / `Bots:ExogShock:*` appsettings key by the `__`→`:` rule (§2). **Rolling one back = delete its line + `up -d server`** (the in-source comment says exactly this; there are `docker-compose.prod.yml.pre-*` backups on the box for a full restore).
3. **The `migrate` one-shot** (§6.3).

### 6.3 The migrate one-shot

The runtime image is deliberately **EF-free** (data access is raw SQL Dapper). Migrations therefore run from the SDK **`build`** stage via the existing design-time factory `KseDbContextFactory` (reads `KSE_DB_CONNECTION_STRING`):

```yaml
migrate:
  build: { target: build }
  profiles: ["migrate"]        # a normal `up` never starts it
  command: sh -c "dotnet tool install -g dotnet-ef … && dotnet ef database update …"
```

Because it's behind a **profile**, a plain `up` skips it. Run it explicitly, once, before the first seeded boot and after any pull that adds migrations:
```
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production run --rm migrate
```

**Two migration paths coexist, by design:** this profiled one-shot *and* the boot-time `Db:AutoMigrate` fallback in `Program.cs` (§1.2). AutoMigrate covers a host where the EF tool is unavailable; the one-shot is the explicit runbook step. Set `Db:AutoMigrate=false` to force the manual path (e.g. multi-instance).

---

## 7. CADDY + ENV + SECRETS

- **`Caddyfile`** — one site block: `{$DOMAIN} { reverse_proxy server:8080; encode gzip; log … }`. Caddy **terminates TLS** (automatic Let's Encrypt) and reverse-proxies to the server container over the compose network. `{$DOMAIN}` is read from the `DOMAIN` env var. Caddy provisions the cert itself at startup and auto-renews; the `caddydata` volume persists it (so recreating the container doesn't re-hit Let's Encrypt rate limits).
- **`.env.production.example`** — the template (`.env.production` itself is gitignored). Copy it, fill real values: `POSTGRES_*` creds, `KSE_DB_CONNECTION_STRING` (password **must match** `POSTGRES_PASSWORD`, `Host=postgres`), `KSE_AUTH_SIGNING_KEY` (`openssl rand -hex 32` — this becomes `Auth__SigningKey`; boot refuses the dev key in prod), `KSE_ALLOWED_ORIGIN`, `DOMAIN`. Compose reads it via `--env-file .env.production`.

---

## 8. DEPLOY + ROLLBACK

**How a code change reaches the box** (from `deploy/RUNBOOK.md` — reference, don't absorb):
```
git pull && docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d --build
```
`up -d --build` rebuilds the changed image(s) and recreates only what changed; unchanged containers (postgres) stay up. Run the `migrate` one-shot **first** if the pull added migrations. To rebuild just the app: `up -d --build server`.

**Rollback patterns, cheapest first:**
- **Config/experiment flag** — delete or edit the `Bots__…` line in `docker-compose.prod.yml`, `docker compose … up -d server`. No rebuild, no data touch. This is the standard "roll a lever back" (§6.2).
- **Code** — `git checkout <prev tip>` + `up -d --build server`.
- **Full/data** — restore the `pg_dump` backup (`deploy/backup.sh`, cron 02:00 UTC) + checkout the pre-change tip + rebuild. Required for anything that touched the DB shape/seed.

**Verify after deploy:** `curl https://$DOMAIN/healthz/ready` → 200 with the DB entry; `curl https://$DOMAIN/api/version`; a burst of orders → ~60 ok + ~40 `429` (rate limit alive); `docker compose logs -f server` for CK/conservation errors (see ENGINE_MECHANICS §5.4).

---

## 9. INDEXES

### 9.1 Operational config gates (flip + restart)

| Key | Default (base) | Effect |
|---|---|---|
| `Bots:AutoStart` | true | Bot loop runs (vs dormant) |
| `Retention:Enabled` | true (base + prod) | History prune loop |
| `Db:AutoMigrate` | true (code default; no appsettings key) | Apply pending EF migrations at boot |
| `Seed:AutoOnEmptyDb` | true (base + prod; code default false) | Seed an empty DB from the embedded workbook |
| `Auth:SigningKey` | dev key (base) | **Must** be overridden in prod or boot throws |
| `Db:GroupCommit:Enabled` / `Db:PerCurrencyGroupGates` | false | Settlement write-behind / per-currency gate split (see appsettings comments + ENGINE_MECHANICS §6) |

### 9.2 Env → appsettings mapping (the `__` rule)

| Env var (compose / `.env.production`) | Config key |
|---|---|
| `Auth__SigningKey` | `Auth:SigningKey` |
| `Cors__AllowedOrigins__0` | `Cors:AllowedOrigins[0]` |
| `KSE_DB_CONNECTION_STRING` | read directly via `Environment.GetEnvironmentVariable` by `PostgresConnectionFactory` (resolution order: `ConnectionStrings:DefaultConnection` → this env var → local-dev default) *and* by `KseDbContextFactory` (migrations). Not a `Section__Key`. |
| `ASPNETCORE_ENVIRONMENT` | selects `appsettings.{Environment}.json` |
| `Bots__Mood__Enabled` | `Bots:Mood:Enabled` |
| `Bots__ExogShock__GlobalCoFireNotionalFrac` | `Bots:ExogShock:GlobalCoFireNotionalFrac` |

Rule: `__` → `:`, trailing `__N` → array index. Env beats both appsettings tiers.

### 9.3 Glossary

- **Section__Key** — env-var form of a config path; double-underscore is the `:` separator on Linux.
- **`!reset`** — Compose ≥ 2.24 override tag that *removes* an inherited list value instead of appending to it (used to un-publish Postgres in prod).
- **migrate one-shot** — profiled `migrate` service that runs `dotnet ef database update` from the SDK build image, because the runtime image has no EF tooling.
- **AutoMigrate** — the belt-and-suspenders boot-time migration in `Program.cs`, independent of the EF CLI tool.
- **liveness vs readiness** — `/healthz/live` = process up (restart signal); `/healthz/ready` = DB reachable (orchestration).

### 9.4 Runbook pointers (reference — not duplicated here)

- **`deploy/RUNBOOK.md`** — full Phase-7e VM provisioning, first cutover (migrate → seed → up), TLS/firewall notes, backup cron, the retention enable procedure.
- **`docs/RESEED_RUNBOOK.md`** — the attended candle-preserving re-anchor reseed (pg_dump → export actual prices → regenerate workbook → `reanchor.sql` → subset-seed via a temp `Bots__AutoStart=false` server → restart with the open-taker-ramp env override → gap-fill the downtime hole → CK/continuity gates → rollback). Note the reseed is the one time the `Bots__Activity__Composition__OpenRamp*` env overrides are armed on the box.
- **`docs/explainers/ENGINE_MECHANICS.md` / `docs/explainers/BOT_MECHANICS.md`** — what the process the box hosts actually *does*.
