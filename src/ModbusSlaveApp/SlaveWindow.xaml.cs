// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModbusCore.Common;
using ModbusTool.App.Services;
using ModbusTool.App.Views;
using ToolApp = ModbusTool.App.App;

namespace ModbusSlaveApp;

public partial class SlaveWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private WindowPlacement? _placement;
    private int _navIndex;
    private bool _cleanupComplete;

    public SlaveWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{typeof(SlaveWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        Loaded += OnLoaded;
        Closed += (_, _) => _statusTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _placement = WindowPlacementStore.Load("ModbusSlave");
        ApplyPlacement();
        _navIndex = Math.Clamp(_placement?.NavigationIndex ?? 0, 0, 2);
        SyncNavigation(_navIndex);
        if (_placement is not null)
        {
            SlaveViewControl.ApplyState(_placement.Sections, _placement.ColumnWidths);
        }

        SlaveViewControl.SetActivePage(_navIndex);
        UpdateStatusBar();
        _statusTimer.Start();
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || SlaveViewControl is null || sender is not RadioButton radio)
        {
            return;
        }

        _navIndex = radio.TabIndex;
        SlaveViewControl.SetActivePage(_navIndex);
        UpdateStatusBar();
    }

    private void SyncNavigation(int index)
    {
        if (NavSlave.Parent is not Panel panel)
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

    private void UpdateStatusBar()
    {
        ModbusTool.App.ViewModels.SlaveViewModel viewModel = SlaveViewControl.ViewModel;
        RunStatusText.Text = viewModel.IsRunning ? "监听：运行中" : "监听：已停止";
        ActivityStatusText.Text = $"状态：{viewModel.StatusMessage}";
        PacketStatusText.Text = $"报文：{SlaveViewControl.PacketMonitor.EntryCount}";
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        new HelpWindow("ModbusSlave", "从站") { Owner = this }.ShowDialog();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        new AboutWindow("ModbusSlave", "从站") { Owner = this }.ShowDialog();
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
        SlaveViewControl.CaptureState(sections, columns);

        WindowPlacementStore.Save("ModbusSlave", new WindowPlacement
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
            await SlaveViewControl.DisposeAsync();
            ToolApp.Log.Write(ToolLogLevel.Information, "系统", "从站服务已清理，从站窗口关闭");
            await ToolApp.Log.FlushAsync();
        }
        finally
        {
            _cleanupComplete = true;
            await Dispatcher.InvokeAsync(Close);
        }
    }
}
