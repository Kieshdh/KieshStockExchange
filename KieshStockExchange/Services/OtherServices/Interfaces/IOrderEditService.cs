using System.ComponentModel;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.OtherServices.Interfaces;

/// <summary>
/// Coordinates the trade page's "modify-order" panel state. Entry points
/// (chart drag, ✎ button on the Open Orders table) call <see cref="BeginEdit"/>
/// to swap the right-hand panel from PlaceOrder to ModifyOrder; the modify
/// view-model calls <see cref="EndEdit"/> on confirm or cancel to swap back.
/// </summary>
public interface IOrderEditService : INotifyPropertyChanged
{
    /// <summary>The order currently being edited, or null when not in edit mode.</summary>
    Order? EditingOrder { get; }

    /// <summary>
    /// Optional pre-fill price supplied by the entry point — e.g. the price the
    /// user dragged the chart line to. Null when the modify form should use the
    /// order's stored price.
    /// </summary>
    decimal? PrefillPrice { get; }

    /// <summary>True while <see cref="EditingOrder"/> is set.</summary>
    bool IsEditing { get; }

    /// <summary>Swap the panel into modify mode for the given order.</summary>
    void BeginEdit(Order order, decimal? prefillPrice = null);

    /// <summary>
    /// Replace the current prefill price without changing the order being edited.
    /// Used when the user re-drags the same order line while the modify panel is
    /// still open: the panel's price field updates to the new dragged-to value.
    /// No-op when not currently in edit mode.
    /// </summary>
    void UpdatePrefillPrice(decimal newPrice);

    /// <summary>Swap the panel back to place-order mode.</summary>
    void EndEdit();
}
