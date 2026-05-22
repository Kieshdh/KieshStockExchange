using System.Collections.ObjectModel;
using System.Collections.Specialized; 

namespace KieshStockExchange.Views.OtherViews;

public partial class SegmentedTabView : ContentView
{
    #region Tab properties
    // Collection of tabs 
    public static readonly BindableProperty TabsProperty =
        BindableProperty.Create(nameof(Tabs),
            typeof(ObservableCollection<SegmentedTabItem>),
            typeof(SegmentedTabView),
            defaultValueCreator: _ => new ObservableCollection<SegmentedTabItem>(),
            propertyChanged: OnTabsChanged);

    public ObservableCollection<SegmentedTabItem> Tabs
    {
        get => (ObservableCollection<SegmentedTabItem>)GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    // The current active tab
    public static readonly BindableProperty SelectedIndexProperty =
        BindableProperty.Create(nameof(SelectedIndex),
            typeof(int),
            typeof(SegmentedTabView),
            0,
            BindingMode.TwoWay,
            propertyChanged: OnSelectedIndexChanged);

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }
    #endregion

    #region Selected and unselected button Styling
    public static readonly BindableProperty SelectedButtonStyleProperty =
        BindableProperty.Create(nameof(SelectedButtonStyle), typeof(Style), typeof(SegmentedTabView), default(Style));

    public static readonly BindableProperty UnselectedButtonStyleProperty =
        BindableProperty.Create(nameof(UnselectedButtonStyle), typeof(Style), typeof(SegmentedTabView), default(Style));

    public Style SelectedButtonStyle
    {
        get => (Style)GetValue(SelectedButtonStyleProperty);
        set => SetValue(SelectedButtonStyleProperty, value);
    }
    public Style UnselectedButtonStyle
    {
        get => (Style)GetValue(UnselectedButtonStyleProperty);
        set => SetValue(UnselectedButtonStyleProperty, value);
    }
    #endregion

    #region Header alignment
    // Header *container* (the pill Border) alignment
    public static readonly BindableProperty HeaderContainerHorizontalOptionsProperty =
        BindableProperty.Create(
            nameof(HeaderContainerHorizontalOptions),
            typeof(LayoutOptions),
            typeof(SegmentedTabView),
            LayoutOptions.Center, // default matches your current behavior
            propertyChanged: OnHeaderLayoutChanged);

    public LayoutOptions HeaderContainerHorizontalOptions
    {
        get => (LayoutOptions)GetValue(HeaderContainerHorizontalOptionsProperty);
        set => SetValue(HeaderContainerHorizontalOptionsProperty, value);
    }

    // Header *content* (the buttons row) alignment
    public static readonly BindableProperty HeaderContentHorizontalOptionsProperty =
        BindableProperty.Create(
            nameof(HeaderContentHorizontalOptions),
            typeof(LayoutOptions),
            typeof(SegmentedTabView),
            LayoutOptions.Center,
            propertyChanged: OnHeaderLayoutChanged);

    public LayoutOptions HeaderContentHorizontalOptions
    {
        get => (LayoutOptions)GetValue(HeaderContentHorizontalOptionsProperty);
        set => SetValue(HeaderContentHorizontalOptionsProperty, value);
    }

    static void OnHeaderLayoutChanged(BindableObject bindable, object oldVal, object newVal)
    {
        var v = (SegmentedTabView)bindable;

        // Apply immediately if the names exist
        if (v.HeaderPill is not null)
            v.HeaderPill.HorizontalOptions = v.HeaderContainerHorizontalOptions;

        if (v.HeaderStrip is not null)
            v.HeaderStrip.HorizontalOptions = v.HeaderContentHorizontalOptions;
    }

    #endregion

    #region Container styling (pill / rail variants)
    // Overrides the hardcoded SegmentedPillContainerStyle on the HeaderPill
    // Border so callers can switch to a transparent "rail" container without
    // touching the XAML or this view's defaults.
    public static readonly BindableProperty ContainerStyleProperty =
        BindableProperty.Create(nameof(ContainerStyle), typeof(Style), typeof(SegmentedTabView), default(Style),
            propertyChanged: OnContainerStyleChanged);

    public Style? ContainerStyle
    {
        get => (Style?)GetValue(ContainerStyleProperty);
        set => SetValue(ContainerStyleProperty, value);
    }

    static void OnContainerStyleChanged(BindableObject b, object o, object n)
    {
        var v = (SegmentedTabView)b;
        if (v.HeaderPill is null) return;
        if (n is Style style)
            v.HeaderPill.Style = style;
    }

    // When true, each tab's button is wrapped in a Grid with a 2-px BoxView
    // underneath that paints Primary on the active tab, Transparent otherwise.
    // Reproduces the design's "underline rail" look (border-bottom: 2px solid).
    public static readonly BindableProperty ShowUnderlineProperty =
        BindableProperty.Create(nameof(ShowUnderline), typeof(bool), typeof(SegmentedTabView), false,
            propertyChanged: (b, o, n) => ((SegmentedTabView)b).BuildHeaders());

    public bool ShowUnderline
    {
        get => (bool)GetValue(ShowUnderlineProperty);
        set => SetValue(ShowUnderlineProperty, value);
    }

    // Floor for each tab button's WidthRequest. Default 80 produces a
    // comfortable pill; drop to 50 for compact 2-tab pills like Buy/Sell where
    // the labels are short and the panel is narrow.
    public static readonly BindableProperty MinTabWidthProperty =
        BindableProperty.Create(nameof(MinTabWidth), typeof(double), typeof(SegmentedTabView), 80.0,
            propertyChanged: (b, o, n) => ((SegmentedTabView)b).BuildHeaders());

    public double MinTabWidth
    {
        get => (double)GetValue(MinTabWidthProperty);
        set => SetValue(MinTabWidthProperty, value);
    }
    #endregion

    #region Right slot content
    public static readonly BindableProperty HeaderRightContentProperty =
    BindableProperty.Create(nameof(HeaderRightContent), typeof(View), typeof(SegmentedTabView),
        default(View), propertyChanged: (b, o, n) =>
        {
            var v = (SegmentedTabView)b;
            if (v.RightSlot is not null)
                v.RightSlot.Content = (View?)n;
        });

    public View? HeaderRightContent
    {
        get => (View?)GetValue(HeaderRightContentProperty);
        set => SetValue(HeaderRightContentProperty, value);
    }
    #endregion

    public SegmentedTabView()
    {
        InitializeComponent();

        // Seed from defaults; OnHeaderLayoutChanged syncs later changes.
        HeaderPill.HorizontalOptions = HeaderContainerHorizontalOptions;
        HeaderStrip.HorizontalOptions = HeaderContentHorizontalOptions;

        Tabs.CollectionChanged += Tabs_CollectionChanged;

        // Always start on first tab if available
        if (Tabs.Count > 0)
            SelectedIndex = 0;

        // Render initial headers/content
        BuildHeaders();
        UpdateContent();
    }

    #region Tab change commands
    static void OnTabsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (SegmentedTabView)bindable;
        if (oldValue is ObservableCollection<SegmentedTabItem> oldColl)
            oldColl.CollectionChanged -= view.Tabs_CollectionChanged;

        if (newValue is ObservableCollection<SegmentedTabItem> newColl)
            newColl.CollectionChanged += view.Tabs_CollectionChanged;

        // Always reset to first tab when tabs change
        if (view.Tabs.Count > 0)
            view.SelectedIndex = 0;

        view.BuildHeaders();
        view.UpdateContent();
    }

    void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        BuildHeaders();

        // If nothing to show, clear content.
        if (Tabs.Count == 0)
        {
            ContentHost.Content = null;
            return;
        }

        // Always clamp/force to first item when items appear or change.
        // Important: if SelectedIndex is already 0, setting it to 0 won't raise the property changed,
        // so we also manually call UpdateHeaderVisuals/UpdateContent below.
        if (SelectedIndex < 0 || SelectedIndex >= Tabs.Count)
            SelectedIndex = 0;

        // Ensure header + content are in sync even if SelectedIndex hasn't "changed".
        UpdateHeaderVisuals();
        UpdateContent();
    }

    static void OnSelectedIndexChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (SegmentedTabView)bindable;
        view.UpdateHeaderVisuals();
        view.UpdateContent();
    }
    #endregion

    #region Header and Content management
    void BuildHeaders()
    {
        // Detach Clicked handlers regardless of whether buttons are direct
        // children or wrapped in a rail Grid.
        foreach (var child in HeaderStrip.Children)
        {
            if (child is Button oldBtn)
                oldBtn.Clicked -= OnHeaderButtonClicked;
            else if (child is Grid grid)
                foreach (var gc in grid.Children)
                    if (gc is Button gBtn) gBtn.Clicked -= OnHeaderButtonClicked;
        }

        HeaderStrip.Children.Clear();

        // Equal-width buttons within the same segmented control: pick a width
        // sized to the longest label so every button in this collection
        // measures identically.
        int maxChars = Tabs.Count > 0 ? Tabs.Max(t => t.Header?.Length ?? 0) : 0;
        double tabWidth = Math.Max(MinTabWidth, maxChars * 8 + 32);

        for (int i = 0; i < Tabs.Count; i++)
        {
            var btn = new Button
            {
                Text = Tabs[i].Header,
            };
            // MinTabWidth <= 0 → let each button size to its own content
            // (padding + text). MinTabWidth > 0 → keep equal-width pill
            // behaviour driven by the longest label.
            if (MinTabWidth > 0)
                btn.WidthRequest = tabWidth;

            // Apply the unselected style initially (falls back safely if not provided)
            if (UnselectedButtonStyle is not null)
                btn.Style = UnselectedButtonStyle;
            else
            {
                // minimal sensible defaults if no style is supplied
                btn.Padding = new Thickness(14, 8);
                btn.CornerRadius = 16;
                btn.BackgroundColor = Colors.Transparent;
                btn.BorderColor = Colors.Gray;
                btn.TextColor = Colors.White;
                btn.BorderWidth = 1;
                btn.FontSize = 14;
            }

            // Remove default MAUI button background ripple where possible
            VisualStateManager.SetVisualStateGroups(btn, new VisualStateGroupList
            {
                new VisualStateGroup
                {
                    Name = "CommonStates",
                    States =
                    {
                        new VisualState { Name = "Normal" },
                        new VisualState { Name = "Pressed" },
                        new VisualState { Name = "Disabled" }
                    }
                }
            });


            btn.BindingContext = i;
            btn.Clicked += OnHeaderButtonClicked;

            if (ShowUnderline)
            {
                // Rail variant: stack the button on top of a 2-px BoxView so
                // the active tab gets a primary-coloured underline.
                var underline = new BoxView
                {
                    HeightRequest      = 2,
                    BackgroundColor    = Colors.Transparent,
                    HorizontalOptions  = LayoutOptions.Fill,
                    VerticalOptions    = LayoutOptions.End,
                };
                var stack = new Grid
                {
                    RowDefinitions =
                    {
                        new RowDefinition(GridLength.Auto),
                        new RowDefinition(GridLength.Auto),
                    },
                };
                Grid.SetRow(btn, 0);
                Grid.SetRow(underline, 1);
                stack.Children.Add(btn);
                stack.Children.Add(underline);
                HeaderStrip.Children.Add(stack);
            }
            else
            {
                HeaderStrip.Children.Add(btn);
            }
        }

        UpdateHeaderVisuals();
    }

    void UpdateHeaderVisuals()
    {
        // For rail mode, look up the active theme's Primary colour once per
        // pass and paint the underline of the selected tab with it.
        Color? primary = null;
        if (ShowUnderline
            && Application.Current?.Resources.TryGetValue("Primary", out var raw) == true
            && raw is Color c)
        {
            primary = c;
        }

        for (int i = 0; i < HeaderStrip.Children.Count; i++)
        {
            Button? btn = null;
            BoxView? underline = null;

            if (HeaderStrip.Children[i] is Grid g)
            {
                foreach (var ch in g.Children)
                {
                    if (ch is Button gb) btn = gb;
                    else if (ch is BoxView bv) underline = bv;
                }
            }
            else if (HeaderStrip.Children[i] is Button direct)
            {
                btn = direct;
            }

            if (btn is null) continue;

            bool isSelected = i == SelectedIndex;

            if (isSelected)
            {
                // Per-tab SelectedButtonStyle wins over the view-level default,
                // so callers can colour individual tabs (e.g. Buy=green, Sell=red).
                var perTab = (i < Tabs.Count) ? Tabs[i].SelectedButtonStyle : null;
                btn.Style = perTab ?? SelectedButtonStyle;
            }
            else
            {
                btn.Style = UnselectedButtonStyle;
            }

            if (underline is not null)
            {
                underline.BackgroundColor = isSelected
                    ? (primary ?? Colors.Transparent)
                    : Colors.Transparent;
            }
        }
    }

    void UpdateContent()
    {
        if (Tabs.Count == 0 || SelectedIndex < 0 || SelectedIndex >= Tabs.Count)
        {
            ContentHost.Content = null;
            return;
        }

        ContentHost.Content = Tabs[SelectedIndex].Content;
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();

        if (Parent == null)
        {
            // Detach Buttons' Clicked handlers
            foreach (var child in HeaderStrip.Children)
            {
                if (child is Button b)
                    b.Clicked -= OnHeaderButtonClicked;
            }

            // Stop listening to the collection
            if (Tabs != null)
                Tabs.CollectionChanged -= Tabs_CollectionChanged;

            // Release content to avoid retaining subviews
            ContentHost.Content = null;

            // Clear right slot
            if (RightSlot is not null) 
                RightSlot.Content = null;
        }
    }

    void OnHeaderButtonClicked(object? sender, EventArgs e)
    {
        if (sender is Button b && b.BindingContext is int index)
            SelectedIndex = index;
    }
    #endregion
}

// Header text + a View as content
public class SegmentedTabItem : BindableObject
{
    #region Header
    public static readonly BindableProperty HeaderProperty =
        BindableProperty.Create(nameof(Header), typeof(string), typeof(SegmentedTabItem), default(string));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }
    #endregion

    #region Content
    public static readonly BindableProperty ContentProperty =
        BindableProperty.Create(nameof(Content), typeof(View), typeof(SegmentedTabItem), default(View));

    public View Content
    {
        get => (View)GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }
    #endregion

    #region SelectedButtonStyle (per-tab override)
    // Lets a single tab override the view-level SelectedButtonStyle so e.g. a
    // Buy/Sell segment can color the active tab green when Buy is selected and
    // red when Sell is selected. When null, SegmentedTabView falls back to its
    // own SelectedButtonStyle.
    public static readonly BindableProperty SelectedButtonStyleProperty =
        BindableProperty.Create(nameof(SelectedButtonStyle), typeof(Style), typeof(SegmentedTabItem), default(Style));

    public Style? SelectedButtonStyle
    {
        get => (Style?)GetValue(SelectedButtonStyleProperty);
        set => SetValue(SelectedButtonStyleProperty, value);
    }
    #endregion
}