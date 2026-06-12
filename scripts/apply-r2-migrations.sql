ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "FlipQuantity" integer NOT NULL DEFAULT 0;
ALTER TABLE "AIUsers" ADD COLUMN IF NOT EXISTS "RoundtripBiasPrc" numeric(20,10) NOT NULL DEFAULT 0.5;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES
    ('20260612120000_AddOrderFlipQuantity', '9.0.0'),
    ('20260612130000_AddBotRoundtripBias', '9.0.0')
ON CONFLICT DO NOTHING;
