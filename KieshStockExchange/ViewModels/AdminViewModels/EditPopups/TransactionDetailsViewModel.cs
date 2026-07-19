using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.ViewModels.OtherViewModels;

namespace KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

public partial class TransactionDetailsViewModel : BaseViewModel, IClosablePopupViewModel
{
    #region Events
    private bool _disposed;

    public event EventHandler? CloseRequested;
    public event EventHandler<int>? NavigateToUserRequested;
    public event EventHandler<int>? NavigateToOrderRequested;
    #endregion

    #region Bound state
    [ObservableProperty] private int _transactionId;
    [ObservableProperty] private string _timestampDisplay = string.Empty;
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _priceDisplay = string.Empty;
    [ObservableProperty] private string _quantityDisplay = string.Empty;
    [ObservableProperty] private string _totalDisplay = string.Empty;
    [ObservableProperty] private string _currencyDisplay = string.Empty;

    [ObservableProperty] private int _buyerId;
    [ObservableProperty] private string _buyerUsername = string.Empty;
    [ObservableProperty] private int _sellerId;
    [ObservableProperty] private string _sellerUsername = string.Empty;
    [ObservableProperty] private int _buyOrderId;
    [ObservableProperty] private int _sellOrderId;
    #endregion

    public TransactionDetailsViewModel()
    {
        Title = "Transaction details";
    }

    #region Initialize and commands
    public void Initialize(Transaction tx, string buyerUsername, string sellerUsername, string symbol)
    {
        if (tx is null) throw new ArgumentNullException(nameof(tx));
        TransactionId = tx.TransactionId;
        TimestampDisplay = tx.TimestampDisplay;
        Symbol = symbol;
        PriceDisplay = tx.PriceDisplay;
        QuantityDisplay = tx.Quantity.ToString();
        TotalDisplay = tx.TotalAmountDisplay;
        CurrencyDisplay = tx.CurrencyType.ToString();
        BuyerId = tx.BuyerId;
        BuyerUsername = buyerUsername;
        SellerId = tx.SellerId;
        SellerUsername = sellerUsername;
        BuyOrderId = tx.BuyOrderId;
        SellOrderId = tx.SellOrderId;
    }

    [RelayCommand]
    private void ViewBuyer()
    {
        if (BuyerId <= 0) return;
        NavigateToUserRequested?.Invoke(this, BuyerId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ViewSeller()
    {
        if (SellerId <= 0) return;
        NavigateToUserRequested?.Invoke(this, SellerId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ViewBuyOrder()
    {
        if (BuyOrderId <= 0) return;
        NavigateToOrderRequested?.Invoke(this, BuyOrderId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ViewSellOrder()
    {
        if (SellOrderId <= 0) return;
        NavigateToOrderRequested?.Invoke(this, SellOrderId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    #endregion

    // Drop handler refs so the closed popup can be collected; no external subscriptions.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseRequested = null;
        NavigateToUserRequested = null;
        NavigateToOrderRequested = null;
    }
}
