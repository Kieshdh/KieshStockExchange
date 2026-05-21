using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Views.OtherViews;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.OtherViewModels;

public partial class TopNavBarViewModel : BaseViewModel, IDisposable
{
    #region Fields
    private const int InboxCapacity = 50;

    private readonly IUserPortfolioService _portfolio;
    private readonly IUserSessionService _session;
    private readonly INotificationService _notify;
    private bool _disposed;
    #endregion

    #region Observable Properties
    [ObservableProperty] private string _fundsDisplay = "$ —";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    public ObservableCollection<Notification> Inbox { get; } = new();
    #endregion

    #region Constructor
    public TopNavBarViewModel(IUserPortfolioService portfolio, IUserSessionService session,
        INotificationService notify)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _session   = session   ?? throw new ArgumentNullException(nameof(session));
        _notify    = notify    ?? throw new ArgumentNullException(nameof(notify));

        _portfolio.SnapshotChanged += OnPortfolioChanged;
        _session.SnapshotChanged   += OnSessionChanged;
        _notify.NotificationAdded  += OnNotificationAdded;

        // Hydrate from the service ring buffer so a freshly-navigated page
        // doesn't show an empty inbox just because its VM was constructed late.
        foreach (var n in _notify.Recent) Inbox.Add(n);
        UnreadCount = Inbox.Count(n => !n.IsRead);

        UpdateFundsDisplay();
    }
    #endregion

    #region Funds Chip
    private void OnPortfolioChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(UpdateFundsDisplay);

    private void OnSessionChanged(object? sender, SessionSnapshot e) =>
        MainThread.BeginInvokeOnMainThread(UpdateFundsDisplay);

    private void UpdateFundsDisplay()
    {
        var currency = _session.BaseCurrency;
        var fund = _portfolio.GetFundByCurrency(currency);
        FundsDisplay = fund == null
            ? CurrencyHelper.Format(0m, currency)
            : CurrencyHelper.Format(fund.AvailableBalance, currency);
    }
    #endregion

    #region Inbox
    private void OnNotificationAdded(object? _, Notification n)
    {
        // NotificationService dispatches to the UI thread.
        Inbox.Insert(0, n);
        while (Inbox.Count > InboxCapacity)
            Inbox.RemoveAt(Inbox.Count - 1);
        UnreadCount++;
    }

    [RelayCommand]
    private async Task ShowInboxAsync()
    {
        // Mark before showing so the badge clears the instant the popup appears.
        MarkAllRead();

        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        await page.ShowPopupAsync(new InboxPopup(this));
    }

    [RelayCommand]
    private void MarkAllRead()
    {
        foreach (var n in Inbox) n.IsRead = true;
        UnreadCount = 0;
    }

    [RelayCommand]
    private void ClearInbox()
    {
        Inbox.Clear();
        UnreadCount = 0;
        _notify.Clear();
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
        _session.SnapshotChanged   -= OnSessionChanged;
        _notify.NotificationAdded  -= OnNotificationAdded;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    #endregion
}
