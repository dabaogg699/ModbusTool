// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ModbusTool.App.Controls;
using ModbusTool.App.Services;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

/// <summary>
/// 单个主站会话的页面宿主：左侧导航通过 <see cref="SetActivePage"/> 切换独立视图。
/// </summary>
public partial class MasterView : UserControl, IAsyncDisposable
{
    private readonly MasterViewModel _viewModel = new();
    private readonly PacketMonitor _packetMonitor;
    private bool _disposed;

    public MasterView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _packetMonitor = PacketView;
        _packetMonitor.SetBinding(PacketMonitor.IsTcpProperty, new Binding(nameof(MasterViewModel.IsTcpConnection))
        {
            Source = _viewModel
        });
        _viewModel.FrameSent += _packetMonitor.AddSent;
        _viewModel.FrameReceived += _packetMonitor.AddReceived;
        _viewModel.ErrorOccurred += OnErrorOccurred;
    }

    public MasterViewModel ViewModel => _viewModel;

    public PacketMonitor PacketMonitor => _packetMonitor;

    public int ActivePage { get; private set; }

    public void SetActivePage(int index)
    {
        ActivePage = Math.Clamp(index, 0, 4);
        DebugPage.Visibility = ActivePage == 0 ? Visibility.Visible : Visibility.Collapsed;
        PacketPage.Visibility = ActivePage == 1 ? Visibility.Visible : Visibility.Collapsed;
        TrendPage.Visibility = ActivePage == 2 ? Visibility.Visible : Visibility.Collapsed;
        ManualPage.Visibility = ActivePage == 3 ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility = ActivePage == 4 ? Visibility.Visible : Visibility.Collapsed;
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
        _viewModel.FrameSent -= _packetMonitor.AddSent;
        _viewModel.FrameReceived -= _packetMonitor.AddReceived;
        _viewModel.ErrorOccurred -= OnErrorOccurred;
        await _viewModel.DisposeAsync().ConfigureAwait(false);
    }

    private void OnErrorOccurred(string message)
    {
        MessageBox.Show(Window.GetWindow(this), message, "主站操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
