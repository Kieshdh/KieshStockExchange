using System.Text.Json.Serialization;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using SQLitePCL;

// Make the bundled e_sqlite3 native library loadable on every TFM the server runs on.
// Mirrors what MauiProgram does on the client side.
Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

// Controllers + System.Text.Json options. JsonStringEnumConverter serialises
// CurrencyType/CandleResolution and other Shared enums as their string names so
// the wire payload survives enum reordering across client/server versions.
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// OpenAPI surface for dev. No auth wiring yet — Phase 5 handles JWT.
builder.Services.AddOpenApi();

// Persistence — owns the SQLite connection for the entire server process. Singleton:
// DBService maintains AsyncLocal transaction stacks + a writer-serialising semaphore,
// both of which need a single instance to function correctly.
builder.Services.AddSingleton<IDataBaseService, DBService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Cheap liveness probe; client uses it during startup to fail fast if the server
// isn't reachable instead of waiting for the first DB call to time out.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapControllers();

app.Run();
