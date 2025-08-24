using System.Windows.Input;

namespace KieshStockExchange.Views.OtherViews;

public partial class TopNavBarView : ContentView
{

	// Title property
    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(TopNavBarView), default(string));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    // Navigation commands
    public ICommand NavigateAccountCommand { get; }
    public ICommand NavigatePortfolioCommand { get; }
    public ICommand NavigateTrendingCommand { get; }
    public ICommand NavigateTradeCommand { get; }
    public ICommand NavigateAdminCommand { get; }

    public TopNavBarView()
    {
        InitializeComponent();

        // Initilize Navigation
        NavigateAccountCommand = new Command(async () => 
            await Shell.Current.GoToAsync("///AccountPage"));
        NavigatePortfolioCommand = new Command(async () => 
            await Shell.Current.GoToAsync("///PortfolioPage"));
        NavigateTrendingCommand = new Command(async () => 
            await Shell.Current.GoToAsync("///StocksPage"));
        NavigateTradeCommand = new Command(async () => 
            await Shell.Current.GoToAsync("///TradePage"));
        NavigateAdminCommand = new Command(async () =>
            await Shell.Current.GoToAsync("///AdminPage"));

        BindingContext = this;
    }
}
    