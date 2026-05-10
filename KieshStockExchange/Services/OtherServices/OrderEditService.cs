using System.ComponentModel;
using System.Runtime.CompilerServices;
using KieshStockExchange.Models;
using KieshStockExchange.Services.OtherServices.Interfaces;

namespace KieshStockExchange.Services.OtherServices;

/// <summary>
/// Singleton implementation of <see cref="IOrderEditService"/>. State changes
/// fire PropertyChanged on the UI thread because all callers are UI-thread
/// VM commands or pointer handlers — no internal marshalling needed.
/// </summary>
public sealed class OrderEditService : IOrderEditService
{
    private Order? _editingOrder;
    private decimal? _prefillPrice;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Order? EditingOrder => _editingOrder;
    public decimal? PrefillPrice => _prefillPrice;
    public bool IsEditing => _editingOrder is not null;

    public void BeginEdit(Order order, decimal? prefillPrice = null)
    {
        ArgumentNullException.ThrowIfNull(order);

        _editingOrder = order;
        _prefillPrice = prefillPrice;

        Raise(nameof(EditingOrder));
        Raise(nameof(PrefillPrice));
        Raise(nameof(IsEditing));
    }

    public void EndEdit()
    {
        if (_editingOrder is null) return;

        _editingOrder = null;
        _prefillPrice = null;

        Raise(nameof(EditingOrder));
        Raise(nameof(PrefillPrice));
        Raise(nameof(IsEditing));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
