# KieshStockExchange.Server

ASP.NET Core host for the exchange engine, bots, database, and SignalR push.

## JWT signing key

The server signs JWTs with `Auth:SigningKey`. Development uses the checked-in key in
`appsettings.Development.json`. **Production refuses to boot with that dev key** — supply a
real one.

Generate a key:

```bash
openssl rand -hex 32
```

Provide it to the server via either:

- **User secrets** (local, non-Production):
  ```bash
  dotnet user-secrets set "Auth:SigningKey" "<generated-key>"
  ```
- **Environment variable** (Production / containers): set `Auth__SigningKey=<generated-key>`.
  The default configuration provider binds `Auth__SigningKey` into `Auth:SigningKey`.

## Ops endpoints

- `GET /api/version` — build/version/uptime (anonymous).
- `GET /healthz/live` — liveness (always 200 while the process is up).
- `GET /healthz/ready` — readiness (200 only once the database answers).

## Rate limits

- `orders` policy (60/min per user, IP fallback) on order place/modify/cancel/cancel-batch
  and portfolio deposit-withdraw/convert.
- `auth` policy (10/min per IP) on login.

Reads are unlimited. Over-limit requests get `429`.
