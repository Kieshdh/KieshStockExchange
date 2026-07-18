using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

public sealed class OrderLinkedTransactionRow
{
    public Transaction Transaction { get; }
    public string Summary { get; }
    public IRelayCommand OpenCommand { get; }

    public OrderLinkedTransactionRow(Transaction tx, Action<int> onOpen)
    {
        Transaction = tx;
        Summary = $"Tx #{tx.TransactionId} • {tx.Quantity} @ {tx.PriceDisplay} • {tx.TimestampShort}";
        OpenCommand = new RelayCommand(() => onOpen(tx.TransactionId));
    }
}
