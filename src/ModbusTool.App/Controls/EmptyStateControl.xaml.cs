// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using System.Windows.Controls;

namespace ModbusTool.App.Controls;

public partial class EmptyStateControl : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(EmptyStateControl), new PropertyMetadata("\uE8A5"));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStateControl), new PropertyMetadata("暂无数据"));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyStateControl), new PropertyMetadata(string.Empty));

    public EmptyStateControl()
    {
        InitializeComponent();
    }

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
