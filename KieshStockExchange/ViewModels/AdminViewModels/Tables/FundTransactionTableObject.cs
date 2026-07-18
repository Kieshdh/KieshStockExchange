using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public sealed class FundTransactionTableObject
{
    public FundTransaction Transaction { get; }
    public string OwnerName { get; }

    public FundTransactionTableObject(FundTransaction transaction, string ownerName)
    {
        Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        OwnerName = ownerName;
    }
}
