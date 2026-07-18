using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class TransactionTableObject : ObservableObject
{
    public Transaction Transaction { get; }
    public User Buyer { get; }
    public User Seller { get; }
    public Stock Stock { get; }

    public IAsyncRelayCommand DetailsCommand { get; }

    public TransactionTableObject(Transaction transaction, User buyer, User seller, Stock stock,
        Func<Transaction, User, User, Stock, Task> onDetails)
    {
        Transaction = transaction;
        Buyer = buyer;
        Seller = seller;
        Stock = stock;
        DetailsCommand = new AsyncRelayCommand(() => onDetails(transaction, buyer, seller, stock));
    }
}
