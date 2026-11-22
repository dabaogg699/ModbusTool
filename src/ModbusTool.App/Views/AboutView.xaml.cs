// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using System.Windows.Controls;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

public partial class AboutView : UserControl
{
    public static readonly DependencyProperty ProductNameProperty = DependencyProperty.Register(
        nameof(ProductName),
        typeof(string),
        typeof(AboutView),
        new PropertyMetadata("ModbusTool", OnProductInfoChanged));

    public static readonly DependencyProperty ProductKindProperty = DependencyProperty.Register(
        nameof(ProductKind),
        typeof(string),
        typeof(AboutView),
        new PropertyMetadata("主站", OnProductInfoChanged));

    public AboutView()
    {
        InitializeComponent();
        Refresh();
    }

    public string ProductName
    {
        get => (string)GetValue(ProductNameProperty);
        set => SetValue(ProductNameProperty, value);
    }

    public string ProductKind
    {
        get => (string)GetValue(ProductKindProperty);
        set => SetValue(ProductKindProperty, value);
    }

    private static void OnProductInfoChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((AboutView)sender).Refresh();
    }

    private void Refresh()
    {
        DataContext = new AboutViewModel(ProductName, ProductKind);
    }
}
