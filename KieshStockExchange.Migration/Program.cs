using KieshStockExchange.Migration;

if (args.Length == 0)
{
    PrintUsage();
    return 64;
}

return args[0] switch
{
    "smoke"        => await RunSmokeAsync(args),
    "migrate-data" => RunMigrateData(args),
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

static int RunMigrateData(string[] _)
{
    Console.Error.WriteLine("migrate-data: not implemented yet.");
    return 64;
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
