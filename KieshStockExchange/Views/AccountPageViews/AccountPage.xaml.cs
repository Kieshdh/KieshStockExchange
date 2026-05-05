using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class AccountPage : ContentPage
{
    private readonly AccountViewModel _vm;
    private readonly IThemeService    _theme;

    public AccountPage(AccountViewModel vm, IThemeService theme)
    {
        InitializeComponent();
        _vm    = vm    ?? throw new ArgumentNullException(nameof(vm));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        BindingContext = _vm;

        ThemePicker.ItemsSource = (System.Collections.IList)_theme.AvailableThemes;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ThemePicker.SelectedItem = _theme.AvailableThemes.FirstOrDefault(t => t.Key == _theme.CurrentThemeKey);
        ThemePicker.SelectedIndexChanged += OnThemePickerChanged;
        _theme.ThemeChanged += OnThemeChangedExternally;
        _vm.Refresh();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ThemePicker.SelectedIndexChanged -= OnThemePickerChanged;
        _theme.ThemeChanged -= OnThemeChangedExternally;
    }

    private void OnThemePickerChanged(object? sender, EventArgs e)
    {
        if (ThemePicker.SelectedItem is ThemeOption opt && opt.Key != _theme.CurrentThemeKey)
            _theme.ApplyTheme(opt.Key);
    }

    private void OnThemeChangedExternally(object? sender, string newKey)
    {
        var match = _theme.AvailableThemes.FirstOrDefault(t => t.Key == newKey);
        if (match != null && !ReferenceEquals(ThemePicker.SelectedItem, match))
            ThemePicker.SelectedItem = match;
    }
}
