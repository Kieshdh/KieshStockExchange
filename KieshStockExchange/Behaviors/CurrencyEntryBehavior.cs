using KieshStockExchange.Helpers;

namespace KieshStockExchange.Behaviors;

/// <summary>
/// Money entry UX: shows a currency-formatted value at rest ($10.20) and strips to a plain editable
/// number (10.2) while focused, reformatting on blur. The bound VM string therefore holds the
/// formatted form at rest — price getters parse via <see cref="CurrencyHelper.Parse"/>, which accepts
/// both forms, so the value round-trips either way.
/// </summary>
public sealed class CurrencyEntryBehavior : Behavior<Entry>
{
    public static readonly BindableProperty CurrencyProperty = BindableProperty.Create(
        nameof(Currency), typeof(CurrencyType), typeof(CurrencyEntryBehavior), CurrencyType.USD);

    public CurrencyType Currency
    {
        get => (CurrencyType)GetValue(CurrencyProperty);
        set => SetValue(CurrencyProperty, value);
    }

    protected override void OnAttachedTo(Entry entry)
    {
        base.OnAttachedTo(entry);
        entry.Focused += OnFocused;
        entry.Unfocused += OnUnfocused;
        // A Behavior is not in the visual tree, so it does not inherit BindingContext — mirror the
        // entry's so {Binding Selected.Currency} on Currency resolves against the same VM.
        entry.BindingContextChanged += OnBindingContextChanged;
        BindingContext = entry.BindingContext;
    }

    protected override void OnDetachingFrom(Entry entry)
    {
        entry.Focused -= OnFocused;
        entry.Unfocused -= OnUnfocused;
        entry.BindingContextChanged -= OnBindingContextChanged;
        base.OnDetachingFrom(entry);
    }

    private void OnBindingContextChanged(object? sender, EventArgs e)
    {
        if (sender is Entry entry) BindingContext = entry.BindingContext;
    }

    // Focus: strip currency chrome so the user edits a plain number.
    private void OnFocused(object? sender, FocusEventArgs e)
    {
        if (sender is not Entry entry) return;
        entry.Text = CurrencyHelper.Parse(entry.Text, Currency) is decimal v
            ? CurrencyHelper.FormatForEdit(v, Currency)
            : string.Empty;
    }

    // Blur: reformat to the currency (empty stays empty so placeholders show).
    private void OnUnfocused(object? sender, FocusEventArgs e)
    {
        if (sender is not Entry entry) return;
        entry.Text = CurrencyHelper.Parse(entry.Text, Currency) is decimal v
            ? CurrencyHelper.Format(v, Currency)
            : string.Empty;
    }
}
