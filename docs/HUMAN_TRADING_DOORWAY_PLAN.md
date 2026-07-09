# Human-Trading Doorway — build plan (Phase 2, "the last mile")

**Goal / milestone:** a friend, on their own device, signs up → gets seed cash → places an order that
fills against the live 20k-bot market → screenshots their polished P&L page. Reuse the existing engine
(bots and humans already share `OrderController.Place → OrderEntryService → matching → settlement`); NO new
engine code.

## Reconnaissance verdict (2026-07-09, read-only scout)
The two hard parts are **already done and reusable**; the whole gap is the sign-up path.

| Component | Status | Notes |
|-----------|--------|-------|
| Register UI (client) | ✓ exists | `Views/UserViews/RegisterPage.xaml` + `RegisterViewModel` — full form, 18+ check |
| Register (server) | ✗ **missing** | `AuthController` has only `/login`; `UserController.Create` is admin-only |
| Seed-cash provisioning | ✗ **missing** | Funds only come from `ExcelSeedService` at startup; new human gets $0 |
| Order placement (human) | ✓ full pipeline | `ApiOrderEntryClient` → `POST /api/orders/place` → same engine as bots. Self-only auth check. |
| Order execution/settlement | ✓ full pipeline | unchanged |
| Portfolio / P&L page | ✓ polished | holdings, realized+unrealized, allocation pie, multi-currency, live quotes |
| Login / JWT | ✓ working | `AuthController.Login` + `JwtTokenService` |

## The three gaps (all on the front door)
1. **No server `/api/auth/register`.** Client `RegisterViewModel` → `AuthService.RegisterAsync` writes only to
   the *local client DB*; it never reaches the server.
2. **No Fund auto-provisioning** on user creation — a fresh human can't trade with $0.
3. **Client wiring + security landmine.** `RegisterViewModel` must call the new endpoint and auto-login. AND
   `AuthService.cs:69` currently sets every human `IsAdmin = true` — a public sign-up must NOT do that (it would
   grant the bot-fleet admin dashboard / scaler API to any registrant).

## Minimal build (recommended first cut)
1. **Server:** `POST /api/auth/register` on `AuthController` — validates input, creates the User (mirroring how
   `Login` verifies passwords so hashing matches), provisions a Fund with seed cash, returns a `LoginResponse`
   (JWT + userId) so the client auto-logs-in. Humans created **non-admin** (server is authoritative).
2. **Server:** seed-cash provisioning inside the register path (`_db.CreateFund(...)`), amount from config
   (`Users:SeedBalanceUsd`, default per the decision below) so it's tunable without a code change.
3. **Client:** `RegisterViewModel.ExecuteRegister()` → POST to the server endpoint, store JWT, set
   `AuthService.CurrentUser`, navigate to the app; drop the local-only `IsAdmin = true`.
4. **Tests:** register happy-path, duplicate-username rejection, Fund provisioned with the right balance,
   registrant is non-admin, self-only order auth still holds.

**Reversible:** all on branch `feature/bot-market-realism-v2`; nothing deployed. Prod launch = Kiesh-gated
(outward-facing: a public sign-up is hard to un-ring).

## Decisions for Kiesh (shape the code — his product front door)
- **Seed cash:** how much / which currency a new account starts with (default proposal: **$100k USD cash only**,
  config-tunable). Options incl. adding an EUR book or a starter holdings basket.
- **Registration policy:** open public self-service vs. gated (invite code / admin approval). Either way,
  registrants are non-admin. Open = the straightest path to the "friend on their own device" milestone.
- **Scope now:** minimal doorway first vs. also do demo-legibility (bot/strategy narrative) in the same pass.

## Deferred (council 5/5 RESIST until the doorway exists)
sector-pulse (#174), brackets for bots, leaderboards, new asset classes, more realism levers, fleet growth.
