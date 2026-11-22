// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using System.Windows.Controls;
using ModbusTool.App.Controls;
using ModbusTool.App.Services;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

/// <summary>
/// 从站页面宿主：左侧导航通过 <see cref="SetActivePage"/> 切换从站仿真、报文监视器与日志。
/// </summary>
public partial class SlaveView : UserControl, IAsyncDisposable
{
    private readonly SlaveViewModel _viewModel = new();
    private readonly PacketMonitor _packetMonitor;
    private Task? _initialization;
    private bool _disposed;

    public SlaveView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _packetMonitor = PacketView;
        _viewModel.FrameSent += _packetMonitor.AddSent;
        _viewModel.FrameReceived += _packetMonitor.AddReceived;
        _viewModel.ErrorOccurred += OnErrorOccurred;
    }

    public SlaveViewModel ViewModel => _viewModel;

    public PacketMonitor PacketMonitor => _packetMonitor;

    public int ActivePage { get; private set; }

    public void SetActivePage(int index)
    {
        ActivePage = Math.Clamp(index, 0, 2);
        DebugPage.Visibility = ActivePage == 0 ? Visibility.Visible : Visibility.Collapsed;
        PacketPage.Visibility = ActivePage == 1 ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility = ActivePage == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void CaptureState(Dictionary<string, bool> sections, Dictionary<string, Dictionary<string, double>> columns)
    {
        DebugPage.CaptureState(sections, columns);
        GridColumnLayout.Capture(_packetMonitor.PacketGrid, columns);
    }

    public void ApplyState(IReadOnlyDictionary<string, bool> sections, IReadOnlyDictionary<string, Dictionary<string, double>> columns)
    {
        DebugPage.ApplyState(sections, columns);
        GridColumnLayout.Apply(_packetMonitor.PacketGrid, columns);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialization is not null)
        {
            await _initialization;
        }

        _viewModel.FrameSent -= _packetMonitor.AddSent;
        _viewModel.FrameReceived -= _packetMonitor.AddReceived;
        _viewModel.ErrorOccurred -= OnErrorOccurred;
        await _viewModel.DisposeAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initialization ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            OnErrorOccurred(exception.Message);
        }
    }

    private void OnErrorOccurred(string message)
    {
        MessageBox.Show(Window.GetWindow(this), message, "从站操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
