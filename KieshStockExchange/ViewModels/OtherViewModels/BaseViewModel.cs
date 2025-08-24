using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KieshStockExchange.ViewModels.OtherViewModels;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    public bool _isBusy;
    [ObservableProperty]
    public string _title;
}
