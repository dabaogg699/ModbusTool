// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;
using ModbusMaster;
using ModbusTool.App.Controls;
using ModbusTool.App.Services;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

public partial class MasterDebugView : UserControl
{
    private MasterDevice? _dragDevice;
    private Point _dragStart;

    public MasterDebugView()
    {
        InitializeComponent();
        foreach (Expander expander in new[] { StationsExpander, CommExpander, HealthExpander })
        {
            expander.Expanded += OnConfigurationExpanderChanged;
            expander.Collapsed += OnConfigurationExpanderChanged;
        }

        UpdateConfigurationVisibility();
    }

    private MasterViewModel ViewModel => (MasterViewModel)DataContext;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpperScroll.MaxHeight = Math.Min(430, Math.Max(220, e.NewSize.Height * 0.55));
    }

    private void OnConfigurationExpanderChanged(object sender, RoutedEventArgs e) => UpdateConfigurationVisibility();

    private void UpdateConfigurationVisibility()
    {
        Visibility visibility = StationsExpander.IsExpanded || CommExpander.IsExpanded || HealthExpander.IsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpperScroll.Visibility = visibility;
        UpperSplitter.Visibility = visibility;
        Dispatcher.BeginInvoke(new Action(UpdateOverflowHint));
    }

    private void OnUpperScrollChanged(object sender, RoutedEventArgs e) => UpdateOverflowHint();

    private void UpdateOverflowHint()
    {
        UpperOverflowHint.Visibility = UpperScroll.Visibility == Visibility.Visible && UpperScroll.ScrollableHeight > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSummarySectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section })
        {
            return;
        }

        Expander? selected = section switch
        {
            "Stations" => StationsExpander,
            "Communication" => CommExpander,
            "Health" => HealthExpander,
            _ => null
        };
        if (selected is null)
        {
            return;
        }

        bool expand = !selected.IsExpanded;
        StationsExpander.IsExpanded = false;
        CommExpander.IsExpanded = false;
        HealthExpander.IsExpanded = false;
        selected.IsExpanded = expand;
        UpperScroll.ScrollToTop();
    }

    public void CaptureState(Dictionary<string, bool> sections, Dictionary<string, Dictionary<string, double>> columns)
    {
        sections["Master.Stations"] = StationsExpander.IsExpanded;
        sections["Master.Communication"] = CommExpander.IsExpanded;
        sections["Master.Health"] = HealthExpander.IsExpanded;
        GridColumnLayout.Capture(DevicesGrid, columns);
        GridColumnLayout.Capture(BlocksGrid, columns);
        GridColumnLayout.Capture(HealthGrid, columns);
    }

    public void ApplyState(IReadOnlyDictionary<string, bool> sections, IReadOnlyDictionary<string, Dictionary<string, double>> columns)
    {
        if (sections.TryGetValue("Master.Stations", out bool stations))
        {
            StationsExpander.IsExpanded = stations;
        }

        if (sections.TryGetValue("Master.Communication", out bool communication))
        {
            CommExpander.IsExpanded = communication;
        }

        if (sections.TryGetValue("Master.Health", out bool health))
        {
            HealthExpander.IsExpanded = health;
        }

        GridColumnLayout.Apply(DevicesGrid, columns);
        GridColumnLayout.Apply(BlocksGrid, columns);
        GridColumnLayout.Apply(HealthGrid, columns);
    }

    private void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        string? name = ShowInputDialog("新建点位分组", "分组名称：", "新分组");
        if (name is not null)
        {
            ViewModel.AddPointGroup(name);
        }
    }

    private static PointGroupItem? GroupFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as PointGroupItem;

    private void OnGroupAddPointClick(object sender, RoutedEventArgs e)
    {
        if (GroupFromSender(sender) is { } group)
        {
            ViewModel.AddPointToGroup(group);
        }
    }

    private void OnGroupEnableHistoryClick(object sender, RoutedEventArgs e)
    {
        if (GroupFromSender(sender) is { } group)
        {
            ViewModel.SetGroupHistory(group, true);
        }
    }

    private void OnGroupDisableHistoryClick(object sender, RoutedEventArgs e)
    {
        if (GroupFromSender(sender) is { } group)
        {
            ViewModel.SetGroupHistory(group, false);
        }
    }

    private void OnGroupRemovePointsClick(object sender, RoutedEventArgs e)
    {
        if (GroupFromSender(sender) is not { } group || group.Points.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this),
                $"确定删除分组“{group.Name}”内的 {group.Points.Count} 个点位？此操作可撤销需重新导入。",
                "删除分组点位", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            ViewModel.RemoveGroupPoints(group);
        }
    }

    private void OnDevicesEdited(object sender, DataGridCellEditEndingEventArgs e) => NotifyConfigurationEdited("Device");

    private void OnBlocksEdited(object sender, DataGridCellEditEndingEventArgs e) => NotifyConfigurationEdited("Block");

    private void OnPointsEdited(object sender, DataGridCellEditEndingEventArgs e) => NotifyConfigurationEdited("Point");

    private void OnConnectionEdited(object sender, RoutedEventArgs e) => NotifyConfigurationEdited("Connection");

    private void OnConnectionSelectionChanged(object sender, SelectionChangedEventArgs e) => NotifyConfigurationEdited("Connection");

    private void NotifyConfigurationEdited(string section)
    {
        Dispatcher.BeginInvoke(() => ViewModel.NotifyConfigurationEdited(section), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridRow row && sender is DataGrid grid)
        {
            row.IsSelected = true;
            grid.SelectedItem = row.Item;
        }
    }

    private void OnDevicePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.IsRunning)
        {
            _dragDevice = null;
            return;
        }

        _dragStart = e.GetPosition(null);
        _dragDevice = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as MasterDevice;
    }

    private void OnDeviceMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragDevice is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        MasterDevice dragged = _dragDevice;
        _dragDevice = null;
        DragDrop.DoDragDrop((DependencyObject)sender, dragged, DragDropEffects.Move);
    }

    private void OnDeviceDrop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsRunning
            || e.Data.GetData(typeof(MasterDevice)) is not MasterDevice dragged
            || FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not MasterDevice target)
        {
            return;
        }

        int fromIndex = ViewModel.Devices.IndexOf(dragged);
        int toIndex = ViewModel.Devices.IndexOf(target);
        if (fromIndex >= 0 && toIndex >= 0)
        {
            ViewModel.MoveDevice(fromIndex, toIndex);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    private void OnAddPointMenuClick(object sender, RoutedEventArgs e) => ViewModel.AddPointCommand.Execute(null);

    private void OnBatchAddPointsClick(object sender, RoutedEventArgs e) => ViewModel.BatchAddPoints(10);

    private void OnDuplicatePointClick(object sender, RoutedEventArgs e) => ViewModel.DuplicateSelectedPoint();

    private void OnRemovePointClick(object sender, RoutedEventArgs e) => ViewModel.RemoveCommand.Execute("Point");

    private void OnPointDataTypeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse(tag, out ModbusDataType dataType))
        {
            ViewModel.SetSelectedPointDataType(dataType);
        }
    }

    private void OnPointByteOrderClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse(tag, out ModbusByteOrder byteOrder))
        {
            ViewModel.SetSelectedPointByteOrder(byteOrder);
        }
    }

    private async void OnExportSelectedPointClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ExportSelectedPointsAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void OnRegisterValueEditing(object sender, RegisterValueEditingEventArgs e)
    {
        e.Cancel = true;
        try
        {
            await ViewModel.WriteRegisterValueAsync(e.Item, e.ProposedValue);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            ((RegisterGrid)sender).CancelEdit();
        }
    }

    private async void OnRegisterWriteRequested(object sender, RegisterValueEditingEventArgs e)
    {
        string initialValue = ViewModel.SelectedBlock?.Function == ModbusFunction.ReadCoils
            ? e.Item.Value.StartsWith('1') ? "1" : "0"
            : e.OriginalValue == "-" ? "0" : e.OriginalValue;
        string? text = ShowInputDialog("写入原始值", $"地址 {e.Item.Address}（{e.Item.AddressHex}）：", initialValue);
        if (text is null)
        {
            return;
        }

        try
        {
            await ViewModel.WriteRegisterValueAsync(e.Item, text);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void OnWritePointValueClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPoint is null)
        {
            return;
        }

        string? text = ShowInputDialog("写入点位值", $"请输入 {ViewModel.SelectedPoint.Name} 的工程量：", "0");
        if (text is null)
        {
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            MessageBox.Show(Window.GetWindow(this), "请输入有效数值。", "写入点位值", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await ViewModel.WriteSelectedPointValueAsync(value);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void OnEnableAllDevicesClick(object sender, RoutedEventArgs e) => ViewModel.SetAllDevicesEnabled(true);

    private void OnDisableAllDevicesClick(object sender, RoutedEventArgs e) => ViewModel.SetAllDevicesEnabled(false);

    private void OnMoveDeviceUpClick(object sender, RoutedEventArgs e) => ViewModel.MoveSelectedDevice(-1);

    private void OnMoveDeviceDownClick(object sender, RoutedEventArgs e) => ViewModel.MoveSelectedDevice(1);

    private void OnSetDeviceIntervalClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDevice is null)
        {
            return;
        }

        string? text = ShowInputDialog("设置轮询周期",
            $"从站 {ViewModel.SelectedDevice.Name} 轮询周期（ms）：",
            ViewModel.SelectedDevice.PollIntervalMs.ToString(CultureInfo.InvariantCulture));
        if (text is not null && int.TryParse(text, out int interval) && interval >= 1)
        {
            ViewModel.SetSelectedDeviceInterval(interval);
        }
    }

    private void ShowError(string message) =>
        MessageBox.Show(Window.GetWindow(this), message, "主站操作失败", MessageBoxButton.OK, MessageBoxImage.Error);

    private string? ShowInputDialog(string title, string label, string initialValue)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 400,
            Height = 180,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };
        TextBlock labelBlock = new() { Text = label, Margin = new Thickness(12, 14, 12, 6), TextWrapping = TextWrapping.Wrap };
        TextBox input = new() { Text = initialValue, Margin = new Thickness(12, 0, 12, 10) };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12)
        };
        Button confirm = new() { Content = "确定", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        Button cancel = new() { Content = "取消", Width = 80, IsCancel = true };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        StackPanel content = new();
        content.Children.Add(labelBlock);
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
