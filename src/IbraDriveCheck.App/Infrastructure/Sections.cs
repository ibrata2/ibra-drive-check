using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using IbraDriveCheck.App;

namespace IbraDriveCheck.App.Infrastructure;

public sealed class TabDef : ObservableObject
{
    public string Key { get; init; } = "";
    public string TitleKey { get; init; } = "";
    public string Glyph { get; init; } = "";
    public Func<UserControl> Create { get; init; } = () => new UserControl();
    public string Title => Loc.T(TitleKey);
    public void Refresh() => Raise(nameof(Title));
}

/// <summary>قسم في القائمة الجانبية، بداخله تبويبات (صفحات) بسيطة.</summary>
public sealed class SectionDef : ObservableObject
{
    public string Key { get; init; } = "";
    public string TitleKey { get; init; } = "";
    public string Glyph { get; init; } = "";
    public List<TabDef> Tabs { get; init; } = new();
    public string Title => Loc.T(TitleKey);
    public void Refresh() { Raise(nameof(Title)); foreach (var t in Tabs) t.Refresh(); }
}

/// <summary>يعرض تبويبات القسم أعلى الصفحة وينشئ كل صفحة عند أول فتح.</summary>
public sealed class SectionView : UserControl
{
    private readonly SectionDef _def;
    private readonly ListBox _tabs;
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly Dictionary<string, UserControl> _pages = new();

    public SectionView(SectionDef def)
    {
        _def = def;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _tabs = new ListBox
        {
            ItemsSource = def.Tabs,
            ItemContainerStyle = (Style)FindResource("TabPill"),
            Margin = new Thickness(24, 16, 24, 0),
            Visibility = def.Tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_tabs, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        _tabs.ItemsPanel = new ItemsPanelTemplate(panel);
        var item = new FrameworkElementFactory(typeof(StackPanel));
        item.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.FontFamilyProperty, FindResource("IconFont"));
        icon.SetValue(TextBlock.FontSizeProperty, 13.0);
        icon.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(TextBlock.FlowDirectionProperty, FlowDirection.LeftToRight);
        icon.SetBinding(TextBlock.TextProperty, new Binding(nameof(TabDef.Glyph)));
        icon.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TabDef.Title)));
        text.SetValue(TextBlock.FontSizeProperty, 13.5);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
        item.AppendChild(icon);
        item.AppendChild(text);
        _tabs.ItemTemplate = new DataTemplate { VisualTree = item };
        _tabs.SelectionChanged += (_, _) => ShowSelected();
        Grid.SetRow(_tabs, 0);
        Grid.SetRow(_host, 1);
        grid.Children.Add(_tabs);
        grid.Children.Add(_host);
        Content = grid;
        _tabs.SelectedIndex = 0;
    }

    public bool Select(string tabKey)
    {
        var t = _def.Tabs.FirstOrDefault(x => x.Key == tabKey);
        if (t == null) return false;
        _tabs.SelectedItem = t;
        ShowSelected();
        return true;
    }

    public void Activate()
    {
        if (_host.Content is IPageActivated a) a.OnActivated();
    }

    private void ShowSelected()
    {
        if (_tabs.SelectedItem is not TabDef t) return;
        if (!_pages.TryGetValue(t.Key, out var page))
        {
            page = t.Create();
            _pages[t.Key] = page;
        }
        if (_host.Content != page) _host.Content = page;
        if (page is IPageActivated a) a.OnActivated();
    }
}
