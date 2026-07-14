namespace KieshStockExchange.Views.OtherViews;

public partial class TopNavBarView : ContentView
{
    #region Constructor
    public TopNavBarView()
    {
        InitializeComponent();
        WireActiveState();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    #endregion

    #region Page title (mirrors the containing page's Title into the nav bar; the Shell title bar is hidden)
    private void OnLoaded(object? sender, EventArgs e) => UpdatePageTitle();

    private void UpdatePageTitle()
    {
        Element? el = Parent;
        while (el is not null && el is not Page) el = el.Parent;
        var title = (el as Page)?.Title;
        bool has = !string.IsNullOrWhiteSpace(title);
        PageTitleLabel.Text = title ?? string.Empty;
        PageTitleLabel.IsVisible = has;
        PageTitleDivider.IsVisible = has;
    }
    #endregion

    #region Active Link Highlight
    private void WireActiveState()
    {
        if (Shell.Current != null)
            Shell.Current.Navigated += OnShellNavigated;
        UpdateActiveLink();
    }

    private void OnShellNavigated(object? sender, ShellNavigatedEventArgs e) => UpdateActiveLink();

    private void UpdateActiveLink()
    {
        var loc = Shell.Current?.CurrentState?.Location?.OriginalString ?? string.Empty;
        SetLinkActive(MarketLink,    loc.Contains("MarketPage"));
        SetLinkActive(TradeLink,     loc.Contains("TradePage"));
        SetLinkActive(PortfolioLink, loc.Contains("PortfolioPage"));
        SetLinkActive(AccountLink,   loc.Contains("AccountPage"));
        SetLinkActive(AdminLink,     loc.Contains("AdminPage"));
        SetLinkActive(BotsLink,      loc.Contains("BotDashboardPage"));
    }

    private static void SetLinkActive(Button btn, bool isActive)
    {
        var key = isActive ? "NavLinkButtonActive" : "NavLinkButton";
        if (Application.Current?.Resources.TryGetValue(key, out var resource) == true
            && resource is Style style)
        {
            btn.Style = style;
        }
    }
    #endregion

    #region Lifecycle
    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (Shell.Current != null)
            Shell.Current.Navigated -= OnShellNavigated;
    }
    #endregion
}
