using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public interface ILazyTab
{
    Task EnsureInitializedAsync();
    Task RefreshAsync();
}
