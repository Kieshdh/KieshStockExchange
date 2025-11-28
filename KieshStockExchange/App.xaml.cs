using KieshStockExchange.Helpers; 
using KieshStockExchange.Services;

namespace KieshStockExchange;

public partial class App : Application
{
    private readonly IAiTradeService _trade;
    private readonly IPriceSnapshotService _snapshot;
    private readonly IExcelImportService _excel;

    public App(IAiTradeService trade, IPriceSnapshotService snapshot, IExcelImportService excel)
    {
        InitializeComponent();

        _trade = trade ?? throw new ArgumentNullException(nameof(trade));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _excel = excel ?? throw new ArgumentNullException(nameof(excel));

        // Start long-running services once for the whole app
        StartBackgroundServices();
    }

    private void StartBackgroundServices()
    {
        _trade.Configure(
            tradeInterval: TimeSpan.FromSeconds(2),
            onlineCheckInterval: TimeSpan.FromMinutes(1),
            dailyCheckInterval: TimeSpan.FromHours(1),
            reloadAssetsInterval: TimeSpan.FromMinutes(10),
            currencies: new List<CurrencyType>() { CurrencyType.USD }
        );

        // IMPORTANT: fire-and-forget
        _ =  _excel.CheckAndAddDatabases();
        //_ =  _excel.ResetAndAddDatabases();
        //_ = _trade.StartBotAsync();
        _ = _snapshot.Start();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}