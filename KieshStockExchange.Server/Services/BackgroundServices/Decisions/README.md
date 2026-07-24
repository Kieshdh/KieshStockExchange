# Decisions/
Bot trading STRATEGIES — the per-tick "what order does this bot place" logic (Conviction, MarketMaker, Arbitrage, Rotator) + the decision context. Consumes Signals/, Population/, Infra/; writes fills to Telemetry/. Namespace stays `...BackgroundServices.Helpers` (folders organize; namespaces flat by design).
