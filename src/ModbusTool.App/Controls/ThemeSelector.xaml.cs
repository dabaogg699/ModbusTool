// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using ModbusCore.Common;
using ModbusTool.App.Themes;

namespace ModbusTool.App.Controls;

/// <summary>
/// 主题选择、导入与导出控件，主站与从站独立窗口共用，避免界面层重复实现。
/// </summary>
public partial class ThemeSelector : UserControl
{
    private bool _suppressSelection;

    public ThemeSelector()
    {
        InitializeComponent();
        ThemeBox.ItemsSource = ThemeManager.BuiltInThemes;
        ThemeBox.SelectedIndex = 0;
        ThemeNameText.Text = ThemeManager.CurrentThemeName;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ThemeBox.SelectedItem is not ThemeOption option)
        {
            return;
        }

        ThemeManager.Apply(option.Theme);
        ThemeNameText.Text = ThemeManager.CurrentThemeName;
        App.Log.Write(ToolLogLevel.Information, "界面", $"已切换{option.DisplayName}主题");
    }

    private async void OnImportThemeClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "导入自定义主题",
            Filter = "主题文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            ThemeDefinition definition = await ThemeManager.ImportAsync(dialog.FileName);
            ThemeManager.ApplyCustom(definition);
            _suppressSelection = true;
            ThemeBox.SelectedIndex = -1;
            _suppressSelection = false;
            ThemeNameText.Text = ThemeManager.CurrentThemeName;
            App.Log.Write(ToolLogLevel.Information, "界面", $"已导入自定义主题：{ThemeManager.CurrentThemeName}");
        }
        catch (Exception exception)
        {
            App.Log.Write(ToolLogLevel.Error, "界面", "导入主题失败", exception);
            MessageBox.Show(Window.GetWindow(this), exception.Message, "导入主题失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnExportThemeClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出当前主题",
            Filter = "主题文件 (*.json)|*.json",
            FileName = $"ModbusTheme_{ThemeManager.CurrentThemeName}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            await ThemeManager.ExportAsync(dialog.FileName);
            App.Log.Write(ToolLogLevel.Information, "界面", $"已导出主题：{dialog.FileName}");
            MessageBox.Show(Window.GetWindow(this), $"主题已导出：\n{dialog.FileName}", "导出主题",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            App.Log.Write(ToolLogLevel.Error, "界面", "导出主题失败", exception);
            MessageBox.Show(Window.GetWindow(this), exception.Message, "导出主题失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
