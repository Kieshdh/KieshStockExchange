using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

public partial class OrderDetailsViewModel : BaseViewModel
{
    #region Fields, events and Constructor
    private readonly IDataBaseService _db;
    private readonly IOrderExecutionService _execution;
    private readonly ILogger<OrderDetailsViewModel> _logger;

    private Order? _order;

    public event EventHandler? CloseRequested;
    public event EventHandler<int>? NavigateToUserRequested;
    public event EventHandler<int>? NavigateToTransactionRequested;
    #endregion

    #region Bound state
    public ObservableCollection<OrderLinkedTransactionRow> LinkedTransactions { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedTransactions))]
    private int _linkedTransactionsCount;
    public bool HasLinkedTransactions => LinkedTransactionsCount > 0;

    [ObservableProperty] private int _orderId;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _createdDisplay = string.Empty;
    [ObservableProperty] private string _updatedDisplay = string.Empty;
    [ObservableProperty] private string _sideDisplay = string.Empty;
    [ObservableProperty] private string _typeDisplay = string.Empty;
    [ObservableProperty] private string _priceDisplay = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnchor))]
    private string _anchorPriceDisplay = string.Empty;
    [ObservableProperty] private string _quantityDisplay = string.Empty;
    [ObservableProperty] private string _amountFilledDisplay = string.Empty;
    [ObservableProperty] private string _totalDisplay = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _slippageDisplay = string.Empty;
    [ObservableProperty] private string _budgetDisplay = string.Empty;
    [ObservableProperty] private string _currencyDisplay = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isOpenOrder;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool CanCancel => IsOpenOrder;
    public bool HasAnchor => !string.IsNullOrEmpty(AnchorPriceDisplay);
    #endregion

    public OrderDetailsViewModel(IDataBaseService db, IOrderExecutionService execution,
        ILogger<OrderDetailsViewModel> logger)
    {
        Title = "Order details";
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Initialize and commands
    public void Initialize(Order order, string username, string symbol)
    {
        _order = order ?? throw new ArgumentNullException(nameof(order));
        _ = LoadLinkedTransactionsAsync(order.OrderId);
        OrderId = order.OrderId;
        UserId = order.UserId;
        Username = username;
        Symbol = symbol;
        CreatedDisplay = order.CreatedAtDisplay;
        UpdatedDisplay = order.UpdatedAtDisplay;
        SideDisplay = order.SideDisplay;
        TypeDisplay = order.TypeDisplay;
        PriceDisplay = order.PriceDisplay;
        AnchorPriceDisplay = order.AnchorPriceDisplay;
        QuantityDisplay = order.Quantity.ToString();
        AmountFilledDisplay = order.AmountFilledDisplay;
        TotalDisplay = order.TotalAmountDisplay;
        Status = order.Status;
        SlippageDisplay = order.SlippageDisplay;
        BudgetDisplay = order.BuyBudgetDisplay;
        CurrencyDisplay = order.CurrencyType.ToString();
        IsOpenOrder = order.IsOpen;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task CancelOrderAsync()
    {
        if (_order is null || !IsOpenOrder) return;
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var result = await _execution.CancelOrderAsync(_order.OrderId);
            if (result is not null && !result.PlacedSuccessfully && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }
            IsOpenOrder = false;
            Status = Order.Statuses.Cancelled;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin cancel from popup failed for order #{OrderId}.", _order.OrderId);
            ErrorMessage = "Cancel failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ViewUser()
    {
        if (UserId <= 0) return;
        NavigateToUserRequested?.Invoke(this, UserId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    #endregion

    #region Helpers
    private async Task LoadLinkedTransactionsAsync(int orderId)
    {
        try
        {
            var txs = await _db.GetTransactionsByOrderId(orderId);
            LinkedTransactions.Clear();
            foreach (var tx in txs)
                LinkedTransactions.Add(new OrderLinkedTransactionRow(tx, RaiseTransactionSelected));
            LinkedTransactionsCount = LinkedTransactions.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OrderDetails: failed to load linked transactions for order #{OrderId}", orderId);
        }
    }

    private void RaiseTransactionSelected(int transactionId)
    {
        NavigateToTransactionRequested?.Invoke(this, transactionId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    #endregion
}
