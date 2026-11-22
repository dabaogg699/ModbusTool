// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ModbusCore.Common;
using ModbusTool.App.Services;
using ModbusTool.App.Views;
using ToolApp = ModbusTool.App.App;

namespace ModbusMasterApp;

public partial class MasterWindow : Window
{
    private readonly string? _initialWorkspacePath;
    private readonly DispatcherTimer _statusTimer;
    private MasterView? _hookedView;
    private WindowPlacement? _placement;
    private int _navIndex;
    private bool _cleanupComplete;

    public MasterWindow()
        : this(null)
    {
    }

    public MasterWindow(string? initialWorkspacePath)
    {
        _initialWorkspacePath = initialWorkspacePath;
        InitializeComponent();
        VersionText.Text = $"v{typeof(MasterWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        Loaded += OnLoaded;
        Closed += (_, _) => _statusTimer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MasterSessionsControl.CurrentViewChanged += OnCurrentViewChanged;
        _placement = WindowPlacementStore.Load("ModbusMaster");
        ApplyPlacement();
        _navIndex = Math.Clamp(_placement?.NavigationIndex ?? 0, 0, 4);
        SyncNavigation(_navIndex);
        ApplyViewState(MasterSessionsControl.CurrentView);
        HookCurrentSession();
        UpdateStatusBar();
        _statusTimer.Start();

        // 启动加载：命令行工作区 → last.mbwork → 内置默认模板。
        if (!string.IsNullOrWhiteSpace(_initialWorkspacePath))
        {
            try
            {
                await MasterSessionsControl.LoadStartupAsync(_initialWorkspacePath);
            }
            catch (Exception exception)
            {
                ToolApp.Log.Write(ToolLogLevel.Error, "主站.操作", "命令行工作区加载失败", exception);
                MessageBox.Show(this, exception.Message, "打开工作区失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            await MasterSessionsControl.LoadStartupAsync(null);
        }
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || MasterSessionsControl is null || sender is not RadioButton radio)
        {
            return;
        }

        _navIndex = radio.TabIndex;
        MasterSessionsControl.CurrentView?.SetActivePage(_navIndex);
        UpdateStatusBar();
    }

    private void SyncNavigation(int index)
    {
        if (NavMaster.Parent is not Panel panel)
        {
            return;
        }

        foreach (RadioButton radio in panel.Children.OfType<RadioButton>())
        {
            if (radio.TabIndex == index)
            {
                radio.IsChecked = true;
            }
        }
    }

    private void OnCurrentViewChanged(MasterView? view)
    {
        HookCurrentSession();
        ApplyViewState(view);
        UpdateStatusBar();
    }

    private void ApplyViewState(MasterView? view)
    {
        if (view is null)
        {
            return;
        }

        if (_placement is not null)
        {
            view.ApplyState(_placement.Sections, _placement.ColumnWidths);
        }

        view.SetActivePage(_navIndex);
    }

    private void HookCurrentSession()
    {
        MasterView? current = MasterSessionsControl.CurrentView;
        if (ReferenceEquals(_hookedView, current))
        {
            return;
        }

        _hookedView = current;
    }

    private void UpdateStatusBar()
    {
        MasterViewModelSnapshot snapshot = MasterViewModelSnapshot.From(MasterSessionsControl.SelectedViewModel);
        SessionStatusText.Text = $"当前会话：{snapshot.Name}";
        CommStatusText.Text = $"通信状态：{snapshot.Status}";
        RunStatusText.Text = snapshot.IsRunning ? "运行中" : "已停止";
        PacketStatusText.Text = $"报文：{MasterSessionsControl.CurrentView?.PacketMonitor.EntryCount ?? 0}";
        CommConfigText.Text = snapshot.Connection;
    }

    private void OnSessionMenuClick(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private void OnRenameSessionClick(object sender, RoutedEventArgs e)
    {
        ModbusTool.App.ViewModels.MasterViewModel? current = MasterSessionsControl.SelectedViewModel;
        if (current is null || !current.IsOffline)
        {
            MessageBox.Show(this, "请先停止当前会话再重命名。", "重命名会话", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Window dialog = new()
        {
            Title = "重命名会话",
            Width = 360,
            Height = 160,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };
        TextBox input = new() { Text = current.Workspace.Name, Margin = new Thickness(14), MinHeight = 28 };
        Button confirm = new() { Content = "确定", Width = 76, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        Button cancel = new() { Content = "取消", Width = 76, IsCancel = true };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 12) };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        StackPanel content = new();
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        confirm.Click += (_, _) =>
        {
            string name = input.Text.Trim();
            if (name.Length == 0)
            {
                return;
            }

            MasterSessionsControl.RenameCurrentSession(name);
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
    }

    private static void OpenMenu(object sender)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnNewSessionClick(object sender, RoutedEventArgs e) => MasterSessionsControl.CreateNewSession();

    private void OnDuplicateSessionClick(object sender, RoutedEventArgs e) => MasterSessionsControl.DuplicateCurrentSession();

    private async void OnCloseSessionClick(object sender, RoutedEventArgs e) => await MasterSessionsControl.CloseCurrentSessionAsync();

    private void OnWorkspaceMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OnResetTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await MasterSessionsControl.ResetToBuiltInTemplateAsync();
        }
        catch (Exception exception)
        {
            ToolApp.Log.Write(ToolLogLevel.Error, "主站.操作", "重置为初始模板失败", exception);
            MessageBox.Show(this, exception.Message, "重置为初始模板", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        new HelpWindow("ModbusMaster", "主站") { Owner = this }.ShowDialog();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        new AboutWindow("ModbusMaster", "主站") { Owner = this }.ShowDialog();
    }

    private void ApplyPlacement()
    {
        WindowPlacement? placement = _placement;
        if (placement is null)
        {
            return;
        }

        if (placement.Width >= MinWidth && placement.Height >= MinHeight)
        {
            Width = placement.Width;
            Height = placement.Height;
        }

        if (SystemParameters.VirtualScreenWidth > 0
            && placement.Left >= SystemParameters.VirtualScreenLeft
            && placement.Top >= SystemParameters.VirtualScreenTop
            && placement.Left + 120 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && placement.Top + 80 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            Left = placement.Left;
            Top = placement.Top;
        }

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        Rect bounds = RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = new Rect(Left, Top, Width, Height);
        }

        Dictionary<string, bool> sections = [];
        Dictionary<string, Dictionary<string, double>> columns = [];
        MasterSessionsControl.CurrentView?.CaptureState(sections, columns);

        WindowPlacementStore.Save("ModbusMaster", new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            NavigationIndex = _navIndex,
            Sections = sections,
            ColumnWidths = columns
        });
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cleanupComplete)
        {
            return;
        }

        SavePlacement();
        e.Cancel = true;
        IsEnabled = false;
        _statusTimer.Stop();
        try
        {
            // 退出前把当前工作区另存为 last.mbwork，下次启动自动恢复。
            await MasterSessionsControl.SaveLastAsync();
            await MasterSessionsControl.DisposeAsync();
            ToolApp.Log.Write(ToolLogLevel.Information, "系统", "主站服务已清理，主站窗口关闭");
            await ToolApp.Log.FlushAsync();
        }
        finally
        {
            _cleanupComplete = true;
            await Dispatcher.InvokeAsync(Close);
        }
    }

    private readonly record struct MasterViewModelSnapshot(string Name, string Status, bool IsRunning, string Connection)
    {
        public static MasterViewModelSnapshot From(ModbusTool.App.ViewModels.MasterViewModel? viewModel)
        {
            if (viewModel is null)
            {
                return new MasterViewModelSnapshot("-", "离线", false, "离线");
            }

            string connection = viewModel.IsTcpConnection
                ? $"TCP {viewModel.Connection.Host}:{viewModel.Connection.Port}"
                : $"RTU {viewModel.Connection.PortName} {viewModel.Connection.BaudRate}";
            return new MasterViewModelSnapshot(viewModel.WorkspaceDisplayName, viewModel.StatusMessage, viewModel.IsRunning, connection);
        }
    }
}
