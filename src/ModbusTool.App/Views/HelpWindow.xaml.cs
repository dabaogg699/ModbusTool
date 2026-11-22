// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

/// <summary>
/// 独立【帮助】弹窗：与【关于】窗口共用卡片式版式与主题资源，按主站/从站加载对应说明。
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow(string productName, string productKind)
    {
        InitializeComponent();
        HelpViewModel viewModel = new(productName, productKind);
        DataContext = viewModel;
        Title = viewModel.Title;
    }
}
