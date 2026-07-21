namespace KieshStockExchange.Views.TradePageViews;

// Inline text-editing wiring for ChartView (partial). The Text/Comment tools drop an anchor, then this
// opens the on-chart Entry overlay (InlineTextEntry in ChartView.xaml) positioned at the click and focused
// for typing — replacing the old modal DisplayPrompt. All the label/delete logic lives in the VM
// (ChartDrawingViewModel.TextEdit.cs); here we only position, focus, and route commit/cancel. Kept
// cross-platform; the Escape-to-cancel key hook is the only Windows-specific bit.
public partial class ChartView
{
    // Vertical nudge so the Entry's text row sits centred on the click point (Entry is ~36 px tall).
    private const double InlineEntryHalfH = 18d;

    // Open the inline editor over a just-placed Text/Comment drawing (p = chart-pixel click point).
    private void StartInlineTextEdit(Guid id, PointF p)
        => _vm?.Drawing.StartInlineEdit(id, p.X, p.Y - InlineEntryHalfH);

    // VM asked us to focus the overlay (it can't touch controls). Dispatch so the Entry has become
    // visible via its IsVisible binding before we grab focus.
    private void OnInlineEditRequested()
        => Dispatcher.Dispatch(() => InlineTextEntry.Focus());

    // Enter commits the label; clicking away (blur) commits too. Both route to the same idempotent command.
    private void OnInlineEntryCompleted(object? sender, EventArgs e)
        => _vm?.Drawing.CommitInlineEditCommand.Execute(null);

    private void OnInlineEntryUnfocused(object? sender, FocusEventArgs e)
        => _vm?.Drawing.CommitInlineEditCommand.Execute(null);

    // Hook Escape → cancel on the platform TextBox once the Entry's handler exists (MAUI Entry surfaces
    // no Escape event of its own). Best-effort + Windows-only; other platforms simply lack the shortcut.
    private void OnInlineEntryLoaded(object? sender, EventArgs e)
    {
#if WINDOWS
        if (InlineTextEntry.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
        {
            el.KeyDown -= OnInlineEntryKeyDown;
            el.KeyDown += OnInlineEntryKeyDown;
        }
#endif
    }

#if WINDOWS
    private void OnInlineEntryKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _vm?.Drawing.CancelInlineEditCommand.Execute(null);
            e.Handled = true;
        }
    }
#endif
}
