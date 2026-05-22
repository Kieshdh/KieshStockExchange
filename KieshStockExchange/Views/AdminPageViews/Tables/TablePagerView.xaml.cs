using System.Collections;
using System.Windows.Input;

namespace KieshStockExchange.Views.AdminPageViews.Tables;

public sealed record PagerEntry(int Page, bool IsActive, bool IsEllipsis)
{
    public bool IsButton => !IsEllipsis;
}

public partial class TablePagerView : ContentView
{
    public static readonly BindableProperty CurrentPageProperty =
        BindableProperty.Create(nameof(CurrentPage), typeof(int), typeof(TablePagerView), 1,
            propertyChanged: OnEntriesInputChanged);

    public static readonly BindableProperty TotalPagesProperty =
        BindableProperty.Create(nameof(TotalPages), typeof(int), typeof(TablePagerView), 1);

    public static readonly BindableProperty PagerSummaryProperty =
        BindableProperty.Create(nameof(PagerSummary), typeof(string), typeof(TablePagerView), string.Empty);

    public static readonly BindableProperty PageSizeProperty =
        BindableProperty.Create(nameof(PageSize), typeof(int), typeof(TablePagerView), 50, BindingMode.TwoWay,
            propertyChanged: OnPageSizeOrAvailableSizesChanged);

    public static readonly BindableProperty AvailablePageSizesProperty =
        BindableProperty.Create(nameof(AvailablePageSizes), typeof(IEnumerable), typeof(TablePagerView), null,
            propertyChanged: OnPageSizeOrAvailableSizesChanged);

    public static readonly BindableProperty VisiblePagesProperty =
        BindableProperty.Create(nameof(VisiblePages), typeof(IList<int>), typeof(TablePagerView), null,
            propertyChanged: OnEntriesInputChanged);

    public static readonly BindableProperty CanGoPrevProperty =
        BindableProperty.Create(nameof(CanGoPrev), typeof(bool), typeof(TablePagerView), false);

    public static readonly BindableProperty CanGoNextProperty =
        BindableProperty.Create(nameof(CanGoNext), typeof(bool), typeof(TablePagerView), false);

    public static readonly BindableProperty GoToPageCommandProperty =
        BindableProperty.Create(nameof(GoToPageCommand), typeof(ICommand), typeof(TablePagerView), null);

    public static readonly BindableProperty GoPrevCommandProperty =
        BindableProperty.Create(nameof(GoPrevCommand), typeof(ICommand), typeof(TablePagerView), null);

    public static readonly BindableProperty GoNextCommandProperty =
        BindableProperty.Create(nameof(GoNextCommand), typeof(ICommand), typeof(TablePagerView), null);

    public static readonly BindableProperty EntriesProperty =
        BindableProperty.Create(nameof(Entries), typeof(IList<PagerEntry>), typeof(TablePagerView),
            Array.Empty<PagerEntry>());

    public int CurrentPage { get => (int)GetValue(CurrentPageProperty); set => SetValue(CurrentPageProperty, value); }
    public int TotalPages { get => (int)GetValue(TotalPagesProperty); set => SetValue(TotalPagesProperty, value); }
    public string PagerSummary { get => (string)GetValue(PagerSummaryProperty); set => SetValue(PagerSummaryProperty, value); }
    public int PageSize { get => (int)GetValue(PageSizeProperty); set => SetValue(PageSizeProperty, value); }
    public IEnumerable? AvailablePageSizes
    {
        get => (IEnumerable?)GetValue(AvailablePageSizesProperty);
        set => SetValue(AvailablePageSizesProperty, value);
    }
    public IList<int>? VisiblePages
    {
        get => (IList<int>?)GetValue(VisiblePagesProperty);
        set => SetValue(VisiblePagesProperty, value);
    }
    public bool CanGoPrev { get => (bool)GetValue(CanGoPrevProperty); set => SetValue(CanGoPrevProperty, value); }
    public bool CanGoNext { get => (bool)GetValue(CanGoNextProperty); set => SetValue(CanGoNextProperty, value); }
    public ICommand? GoToPageCommand { get => (ICommand?)GetValue(GoToPageCommandProperty); set => SetValue(GoToPageCommandProperty, value); }
    public ICommand? GoPrevCommand { get => (ICommand?)GetValue(GoPrevCommandProperty); set => SetValue(GoPrevCommandProperty, value); }
    public ICommand? GoNextCommand { get => (ICommand?)GetValue(GoNextCommandProperty); set => SetValue(GoNextCommandProperty, value); }

    public IList<PagerEntry> Entries
    {
        get => (IList<PagerEntry>)GetValue(EntriesProperty);
        private set => SetValue(EntriesProperty, value);
    }

    public TablePagerView()
    {
        InitializeComponent();
    }

    static void OnEntriesInputChanged(BindableObject b, object oldVal, object newVal)
    {
        if (b is TablePagerView v) v.RebuildEntries();
    }

    static void OnPageSizeOrAvailableSizesChanged(BindableObject b, object oldVal, object newVal)
    {
        if (b is TablePagerView v) v.SyncSizePickerSelection();
    }

    void SyncSizePickerSelection()
    {
        if (AvailablePageSizes is null || SizePicker is null) return;
        int i = 0;
        foreach (var item in AvailablePageSizes)
        {
            if (item is int sz && sz == PageSize)
            {
                if (SizePicker.SelectedIndex != i) SizePicker.SelectedIndex = i;
                return;
            }
            i++;
        }
    }

    void OnSizePickerSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is Picker p && p.SelectedItem is int size && size > 0 && size != PageSize)
            PageSize = size;
    }

    void RebuildEntries()
    {
        var pages = VisiblePages;
        if (pages is null || pages.Count == 0)
        {
            Entries = Array.Empty<PagerEntry>();
            return;
        }

        var result = new List<PagerEntry>(pages.Count * 2);
        int prev = -1;
        foreach (var p in pages)
        {
            if (prev != -1 && p > prev + 1)
                result.Add(new PagerEntry(0, false, true));
            result.Add(new PagerEntry(p, p == CurrentPage, false));
            prev = p;
        }
        Entries = result;
    }
}
