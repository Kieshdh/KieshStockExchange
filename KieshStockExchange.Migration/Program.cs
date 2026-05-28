using KieshStockExchange.Migration;

if (args.Length == 0)
{
    PrintUsage();
    return 64;
}

return args[0] switch
{
    "smoke"        => await RunSmokeAsync(args),
    "migrate-data" => await RunMigrateDataAsync(args),
    _              => UnknownMode(args[0]),
};

static async Task<int> RunSmokeAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: smoke <postgres-conn-str>");
        return 64;
    }
    return await RoundTripSmoke.RunAsync(args[1]) ? 0 : 1;
}

static async Task<int> RunMigrateDataAsync(string[] args)
{
    string? sqlitePath = null, pgConn = null;
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--sqlite") sqlitePath = args[i + 1];
        else if (args[i] == "--pg") pgConn = args[i + 1];
    }
    if (sqlitePath is null || pgConn is null)
    {
        Console.Error.WriteLine("usage: migrate-data --sqlite <path> --pg <conn-str>");
        return 64;
    }
    return await MigrateData.RunAsync(sqlitePath, pgConn) ? 0 : 1;
}

static int UnknownMode(string mode)
{
    Console.Error.WriteLine($"unknown mode: {mode}");
    PrintUsage();
    return 64;
}

static void PrintUsage()
{
    Console.Error.WriteLine("KieshStockExchange.Migration");
    Console.Error.WriteLine("  smoke <postgres-conn-str>");
    Console.Error.WriteLine("  migrate-data --sqlite <path> --pg <conn-str>   (not yet)");
}
