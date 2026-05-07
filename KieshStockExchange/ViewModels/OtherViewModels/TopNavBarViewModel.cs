using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using System.Globalization;

namespace KieshStockExchange.ViewModels.OtherViewModels;

public partial class TopNavBarViewModel : BaseViewModel, IDisposable
{
    #region Fields
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserSessionService _session;
    private bool _disposed;
    #endregion

    #region Observable Properties
    [ObservableProperty] private string _fundsDisplay = "$ —";
    #endregion

    #region Constructor
    public TopNavBarViewModel(IUserPortfolioService portfolio, IUserSessionService session)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _session   = session   ?? throw new ArgumentNullException(nameof(session));

        _portfolio.SnapshotChanged += OnPortfolioChanged;
        UpdateFundsDisplay();
    }
    #endregion

    #region Funds Chip
    private void OnPortfolioChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(UpdateFundsDisplay);

    private void UpdateFundsDisplay()
    {
        var fund = _portfolio.GetBaseFund();
        FundsDisplay = fund == null
            ? "$ —"
            : $"$ {fund.AvailableBalance.ToString("N2", CultureInfo.InvariantCulture)}";
    }
    #endregion

    #region Navigation Commands
    [RelayCommand] private async Task NavigateMarketAsync()    => await Shell.Current.GoToAsync("///MarketPage");
    [RelayCommand] private async Task NavigateTradeAsync()     => await Shell.Current.GoToAsync("///TradePage");
    [RelayCommand] private async Task NavigatePortfolioAsync() => await Shell.Current.GoToAsync("///PortfolioPage");
    [RelayCommand] private async Task NavigateAccountAsync()   => await Shell.Current.GoToAsync("///AccountPage");
    [RelayCommand] private async Task NavigateAdminAsync()     => await Shell.Current.GoToAsync("///AdminPage");
    [RelayCommand] private async Task NavigateBotsAsync()      => await Shell.Current.GoToAsync("///BotDashboardPage");
    #endregion

    #region IDisposable
    public void Dispose()
    {
        if (_disposed) return;
        _portfolio.SnapshotChanged -= OnPortfolioChanged;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    #endregion
}
