// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;

namespace ModbusTool.App.Views;

/// <summary>
/// 独立【关于】弹窗：从窗口右上角按钮打开，不占用主标签栏。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow(string productName, string productKind)
    {
        InitializeComponent();
        About.ProductName = productName;
        About.ProductKind = productKind;
        Title = $"关于 {productName}";
    }
}
