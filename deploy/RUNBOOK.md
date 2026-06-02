# Deploy runbook — Phase 7e

Host-agnostic steps for a Linux VM. Primary target: Oracle Cloud Always Free ARM
(Ampere A1), Ubuntu 24.04; documented fallback if its network storage throttles the
write path: Hetzner CAX31 (same arm64 image, local NVMe). Self-hosted Postgres +
server + Caddy via docker-compose on one box.

> **Oracle-specific networking (⚠️):** two layers must allow tcp 80/443. (1) The VCN
> security list / NSG ingress rules (NOT 5432). (2) The Ubuntu image ships with a
> locked-down iptables — add ACCEPT rules for 80/443 and persist them
> (`netfilter-persistent save`), or the Let's Encrypt challenge and all traffic
> silently fail. A1 capacity ("Out of host capacity") is commonly exhausted — retry
> or pick a quieter region/AD.

## One-time provisioning

1. Create the VM. Harden SSH: key-only auth, root login disabled, `fail2ban` on.
2. Install Docker:
   ```bash
   apt update && apt install -y docker.io docker-compose-plugin
   usermod -aG docker "$USER"   # re-login for group to take effect
   ```
3. Clone the repo:
   ```bash
   git clone <repo-url> /opt/kse-server && cd /opt/kse-server
   ```
4. Create `.env.production` from the template and fill in real secrets:
   ```bash
   cp .env.production.example .env.production
   # POSTGRES_PASSWORD: openssl rand -hex 32
   # KSE_AUTH_SIGNING_KEY: openssl rand -hex 32
   # KSE_DB_CONNECTION_STRING password must match POSTGRES_PASSWORD
   # DOMAIN + KSE_ALLOWED_ORIGIN: your subdomain
   ```
5. DNS: add an A record for `$DOMAIN` → VM public IP. Wait for propagation. A free
   DuckDNS subdomain works (it gives a real A record you control, so Caddy's HTTP-01
   challenge succeeds): set `DOMAIN=<sub>.duckdns.org` and point its IP at the VM.

> **Prod compose invocation.** All production commands stack the prod override on
> top of the base file:
> ```bash
> docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production <cmd>
> ```
> The override (a) does NOT publish Postgres's 5432 to the host (the server reaches
> it over the compose network) and (b) adds a profiled, one-shot `migrate` service.
> A bare `docker compose` (no `-f`) stays the local-dev shape (Postgres on 5432).

## First cutover

6. Bring up Postgres only, then run migrations once via the `migrate` service. The
   runtime image is deliberately EF-free (data access is raw SQL), so migrations run
   from the SDK build stage using the design-time factory (`KseDbContextFactory`):
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d postgres
   docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production run --rm migrate
   ```
   (Migrations are NOT run on every boot — `migrate` is profiled, so a normal `up`
   never starts it. This is the one-shot.)
7. Seed a fresh DB. Migrations must already be applied (step 6) — the empty-check
   queries tables. Use `docker compose run -e` to pass the seed flag to the container
   (a bash-prefix `Seed__AutoOnEmptyDb=true docker compose up` sets the var for the
   compose process, NOT the container, so the server boots without seeding — silent
   failure). Run the server interactively so you can watch the seed banner, then
   stop and switch to detached:
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production \
     run --rm -e Seed__AutoOnEmptyDb=true server   # logs stream; look for "seeding from embedded workbook"
   ```
   Ctrl+C once seeded. The default `Seed:AutoOnEmptyDb=false` in
   appsettings.Production.json takes over for the persistent `up -d` in step 8.
8. Start everything:
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d
   ```
   Caddy fetches a Let's Encrypt cert on the first HTTPS request to `$DOMAIN`.

## Smoke from a client

9. Point the client at production: edit
   `KieshStockExchange/Resources/Raw/appsettings.json` →
   `"Server": { "BaseUrl": "https://<your-domain>" }`, rebuild, run, log in,
   place an order. (No new ServerEndpoint.cs needed — the client already reads
   this asset via LoadServerBaseUrl().)

## Verify

- `curl https://$DOMAIN/api/version` → version JSON, valid LE cert, `Server: Caddy`.
- `curl https://$DOMAIN/healthz/ready` → 200 with the database entry.
- 100 orders/min from a script → ~60 ok + ~40 `429`.
- `docker compose stop server` → client connection banner within ~5s; restart clears it.

## Operations

- Logs: `docker compose logs -f server`, plus rolling files in the `serverlogs` volume.
- Backups: `deploy/backup.sh` (cron at 02:00 UTC) runs `pg_dump`.
- Update: `git pull && docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d --build`
  (run the `migrate` one-shot first if the pull added migrations).

## Database history retention (Wave 8 §3)

A background prune (`RetentionHostedService`) bounds the two high-churn tables and
caps fine-grained candles. It is **off in production by default**
(`Retention:Enabled=false`) — verify with the dry-run endpoint, then flip the flag.

- **Verify before enabling**: as an admin, `GET /api/admin/retention/preview` returns
  the per-table counts that *would* delete (deletes nothing). Sanity-check, then
  `POST /api/admin/retention/run` for a single live cycle. Confirm `Open` orders and
  human-owned rows are untouched and that an old chart range still renders after the
  run (proves the candle backfill→verify→delete gate).
- **Enable**: set `Retention:Enabled=true` (env/appsettings) and restart the server.
  All windows are config-driven — no redeploy needed to tune.
- **Tier-2 candle gate**: before verifying, the prune (a) gap-fills missing fine candles
  (`CandleService.FillCandleGapsAsync` cascades 15s→1m→5m→…, synthesizing absent buckets
  from finer rungs), then (b) runs the upward 5m→15m/1h/4h/1d backfill on the now-denser
  5m. The gate then verifies coverage at `CandleVerifyBucketSeconds` (default 900 = 15m —
  the resolution old-range charts render); drop to 300 (5m) once coverage proves complete.
  `CandleCoverageTolerance` (default 0.95) is the pass threshold against trade-bearing
  windows. `CandleGapFillLookbackDays` (default 60) bounds the gap-fill window per cycle —
  the first cycle against a backlog is the costly one (idempotent after); lower it if that
  cycle runs long, or temporarily set `CandleVerifyBucketSeconds=3600` (1h, complete over
  history) to drain a deep backlog before tightening to 15m. A window that had trades but
  no candle at any finer resolution is unrebuildable and absorbed by the 0.95 tolerance.
- **Command timeout**: `CommandTimeoutSeconds` (default 300) overrides Npgsql's 30s
  default — the boundary/count scans on the 20M-row tables need it. Floored at 30s.
- **Hosting windows**: defaults are generous for Oracle Always Free (24 GB / 200 GB):
  `OrderWindowHours`/`TransactionWindowHours=48`, `CandleFineDays=90`. On a smaller
  Hetzner CAX31 fallback (40–80 GB) tighten via config — set both window hours to
  `24` and `CandleFineDays` to `30`. No code change.
- **Space reclaim**: the per-table autovacuum tuning migration
  (`RetentionAutovacuumTuning`) keeps `Orders`/`Transactions` from bloating once
  deletes ≈ inserts (steady state). It does **not** return disk to the OS — the file
  only shrinks via `VACUUM FULL` (ACCESS EXCLUSIVE lock, run in a maintenance window)
  or online `pg_repack` (needs the extension). With 200 GB free this is rarely urgent.
  Example one-off:
  ```bash
  docker compose --env-file .env.production exec postgres \
    psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c 'VACUUM (FULL, VERBOSE) "Transactions";'
  ```
- **Monitor**: watch `n_dead_tup` per table via `pg_stat_user_tables` during a soak;
  row counts should plateau at the window and on-disk size stop climbing after
  autovacuum settles.
