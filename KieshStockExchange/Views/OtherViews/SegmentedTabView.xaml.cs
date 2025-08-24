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

    public SegmentedTabView()
    {
        InitializeComponent();

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
        foreach (var child in HeaderStrip.Children)
            if (child is Button oldBtn)
                oldBtn.Clicked -= OnHeaderButtonClicked;

        HeaderStrip.Children.Clear();

        for (int i = 0; i < Tabs.Count; i++)
        {
            var btn = new Button { Text = Tabs[i].Header };

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

            HeaderStrip.Children.Add(btn);
        }

        UpdateHeaderVisuals();
    }

    void UpdateHeaderVisuals()
    {
        for (int i = 0; i < HeaderStrip.Children.Count; i++)
        {
            if (HeaderStrip.Children[i] is Button b)
            {
                bool isSelected = i == SelectedIndex;

                if (isSelected)
                    b.Style = SelectedButtonStyle;
                else b.Style = UnselectedButtonStyle;
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
        }
    }

    void OnHeaderButtonClicked(object sender, EventArgs e)
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
}