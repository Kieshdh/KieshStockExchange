# Deploy runbook — Phase 7e

Host-agnostic steps for a Linux VM (recommendation: Hetzner CX22, Ubuntu 24.04).
Self-hosted Postgres + server + Caddy via docker-compose on one box.

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
5. DNS: add an A record for `$DOMAIN` → VM public IP. Wait for propagation.

## First cutover

6. Bring up Postgres only, then run migrations once:
   ```bash
   docker compose --env-file .env.production up -d postgres
   docker compose --env-file .env.production run --rm server dotnet ef database update
   ```
   (Migrations are NOT run on every boot — this is the one-shot.)
7. Seed a fresh DB:
   - Create the first admin user directly in Postgres (INSERT into Users with
     IsAdmin = true and a known password hash), OR temporarily set
     `Seed:AutoOnEmptyDb=true` to auto-seed the embedded workbook on first boot.
   - Then, as that admin, `POST /api/admin/seed/excel/full` (or rely on auto-seed).
8. Start everything:
   ```bash
   docker compose --env-file .env.production up -d
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
- Update: `git pull && docker compose --env-file .env.production up -d --build`.

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
