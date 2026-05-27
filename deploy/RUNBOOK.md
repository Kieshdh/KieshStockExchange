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
