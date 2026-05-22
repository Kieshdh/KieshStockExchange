using System.Windows.Input;

namespace KieshStockExchange.Views.AdminPageViews.Tables;

public partial class SortableHeader : ContentView
{
    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(SortableHeader), string.Empty);

    public static readonly BindableProperty SortKeyProperty =
        BindableProperty.Create(nameof(SortKey), typeof(string), typeof(SortableHeader), string.Empty,
            propertyChanged: OnSortStateChanged);

    public static readonly BindableProperty CurrentSortKeyProperty =
        BindableProperty.Create(nameof(CurrentSortKey), typeof(string), typeof(SortableHeader), null,
            propertyChanged: OnSortStateChanged);

    public static readonly BindableProperty IsDescendingProperty =
        BindableProperty.Create(nameof(IsDescending), typeof(bool), typeof(SortableHeader), true,
            propertyChanged: OnSortStateChanged);

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(SortableHeader), null);

    public static readonly BindableProperty ArrowGlyphProperty =
        BindableProperty.Create(nameof(ArrowGlyph), typeof(string), typeof(SortableHeader), string.Empty);

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string SortKey
    {
        get => (string)GetValue(SortKeyProperty);
        set => SetValue(SortKeyProperty, value);
    }

    public string? CurrentSortKey
    {
        get => (string?)GetValue(CurrentSortKeyProperty);
        set => SetValue(CurrentSortKeyProperty, value);
    }

    public bool IsDescending
    {
        get => (bool)GetValue(IsDescendingProperty);
        set => SetValue(IsDescendingProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public string ArrowGlyph
    {
        get => (string)GetValue(ArrowGlyphProperty);
        private set => SetValue(ArrowGlyphProperty, value);
    }

    public SortableHeader()
    {
        InitializeComponent();
    }

    static void OnSortStateChanged(BindableObject b, object oldVal, object newVal)
    {
        if (b is SortableHeader h) h.UpdateArrow();
    }

    void UpdateArrow()
    {
        bool active = !string.IsNullOrEmpty(SortKey)
                      && string.Equals(SortKey, CurrentSortKey, StringComparison.Ordinal);
        ArrowGlyph = !active ? string.Empty : (IsDescending ? "↓" : "↑");
    }

    void OnTapped(object? sender, TappedEventArgs e)
    {
        if (Command is null || string.IsNullOrEmpty(SortKey)) return;
        if (Command.CanExecute(SortKey)) Command.Execute(SortKey);
    }
}
