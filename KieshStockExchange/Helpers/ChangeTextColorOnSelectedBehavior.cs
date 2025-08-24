using System.Globalization;

namespace KieshStockExchange.Helpers;
public class ChangeTextColorOnSelectedBehavior : Behavior<Border>
{
    public Color SelectedColor { get; set; } = Colors.Black;
    public Color UnselectedColor { get; set; } = Colors.Gray;

    protected override void OnAttachedTo(Border bindable)
    {
        base.OnAttachedTo(bindable);
        bindable.PropertyChanged += OnBorderPropertyChanged;
        Update(bindable);
    }

    protected override void OnDetachingFrom(Border bindable)
    {
        base.OnDetachingFrom(bindable);
        bindable.PropertyChanged -= OnBorderPropertyChanged;
    }

    private void OnBorderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is Border b && (e.PropertyName == nameof(Border.Background) || e.PropertyName == "BindingContext"))
            Update(b);
    }

    private void Update(Border b)
    {
        // Selected when background is white (per our DataTrigger)
        bool isSelected = b.Background is SolidColorBrush scb && scb.Color == Colors.White;
        foreach (var child in GetLabelDescendants(b))
            child.TextColor = isSelected ? SelectedColor : UnselectedColor;
    }

    // Helper method to get all Label descendants of a Border
    private static IEnumerable<Label> GetLabelDescendants(Border border)
    {
        var labels = new List<Label>();
        void Traverse(Element element)
        {
            if (element is Label label)
                labels.Add(label);

            if (element is IElementController controller)
            {
                foreach (var child in controller.LogicalChildren)
                    Traverse(child);
            }
        }
        Traverse(border);
        return labels;
    }
}
