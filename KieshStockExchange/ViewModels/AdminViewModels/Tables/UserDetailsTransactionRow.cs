using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public sealed class UserDetailsTransactionRow
{
    public Transaction Transaction { get; }
    public string Symbol { get; }
    public string SideForUser { get; }
    public IRelayCommand DetailsCommand { get; }

    public UserDetailsTransactionRow(Transaction tx, string symbol, int viewerUserId, Action<int> onDetails)
    {
        Transaction = tx;
        Symbol = symbol;
        SideForUser = tx.BuyerId == viewerUserId ? "BUY" : (tx.SellerId == viewerUserId ? "SELL" : "—");
        DetailsCommand = new RelayCommand(() => onDetails(tx.TransactionId));
    }
}
