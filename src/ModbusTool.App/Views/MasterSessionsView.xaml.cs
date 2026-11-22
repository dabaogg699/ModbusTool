// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ModbusMaster;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

/// <summary>
/// 主站会话容器：所有会话共享窗口导航，会话切换由标题栏下拉框驱动。
/// </summary>
public partial class MasterSessionsView : UserControl, IAsyncDisposable, INotifyPropertyChanged
{
    private readonly List<MasterView> _sessions = [];
    private int _sessionCounter;
    private bool _disposed;
    private MasterView? _current;

    public MasterSessionsView()
    {
        InitializeComponent();
        CreateSession();
    }

    public int SessionCount => _sessions.Count;

    public ObservableCollection<MasterViewModel> SessionViewModels { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public MasterView? CurrentView => _current;

    public MasterViewModel? SelectedViewModel
    {
        get => _current?.ViewModel;
        set
        {
            if (value is null)
            {
                return;
            }

            MasterView? view = _sessions.FirstOrDefault(item => ReferenceEquals(item.ViewModel, value));
            if (view is null || ReferenceEquals(view, _current))
            {
                return;
            }

            _current = view;
            SessionHost.Content = view;
            RaiseSelectionChanged();
            CurrentViewChanged?.Invoke(view);
        }
    }

    public event Action<MasterView?>? CurrentViewChanged;

    public void CreateNewSession() => CreateSession();

    /// <summary>
    /// 加载内置默认模板或用户工作区后的通用处理：校验轮询计划非空并写加载日志（IP/端口/站号/寄存器数）。
    /// 返回 false 表示工作区没有任何可轮询设备（不破坏已加载内容，调用方负责提示）。
    /// </summary>
    private bool ValidateLoadedWorkspace(ModbusTool.App.ViewModels.MasterViewModel viewModel, string sourceName)
    {
        ModbusMaster.MasterWorkspace workspace = viewModel.WorkspaceManager.Current;
        var devices = workspace.Devices.Where(d => d.Enabled).ToList();
        int registerCount = devices.Sum(d => d.ReadBlocks.Sum(b => b.Quantity));

        foreach (ModbusMaster.MasterDevice device in devices)
        {
            App.Log.Write(
                ModbusCore.Common.ToolLogLevel.Information,
                "主站.加载",
                $"{sourceName}：TCP {workspace.Connection.Host}:{workspace.Connection.Port}，站号 {device.UnitId}，" +
                $"{device.ReadBlocks.Count} 个读块，共 {registerCount} 个寄存器",
                (string?)null);
        }

        if (devices.Count == 0 || registerCount == 0)
        {
            App.Log.Write(
                ModbusCore.Common.ToolLogLevel.Warning,
                "主站.加载",
                $"{sourceName} 中没有启用的设备或寄存器，启动轮询前请先配置点位",
                (string?)null);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 启动加载：命令行指定了工作区则由调用方 OpenWorkspaceAsync 处理；
    /// 否则恢复 last.mbwork，不存在时加载内置默认模板。
    /// </summary>
    public async Task LoadStartupAsync(string? initialWorkspacePath)
    {
        MasterView? session = _current ?? _sessions.FirstOrDefault();
        if (session is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(initialWorkspacePath))
        {
            await session.ViewModel.OpenWorkspaceFileAsync(initialWorkspacePath);
            ValidateLoadedWorkspace(session.ViewModel, "命令行工作区");
            return;
        }

        if (System.IO.File.Exists(Services.AppDataPathHelper.LastWorkspaceFile))
        {
            try
            {
                await session.ViewModel.OpenWorkspaceFileAsync(Services.AppDataPathHelper.LastWorkspaceFile);
                ValidateLoadedWorkspace(session.ViewModel, "上次工作区");
                return;
            }
            catch (Exception exception)
            {
                App.Log.Write(ModbusCore.Common.ToolLogLevel.Warning, "界面", "上次工作区加载失败，改用内置默认模板", exception);
            }
        }

        ModbusMaster.MasterWorkspace defaultWorkspace = await Services.DefaultTemplateService.LoadBuiltInAsync();
        session.ViewModel.LoadClonedWorkspace(defaultWorkspace);
        session.ViewModel.RenameWorkspace("默认模板");
        bool usable = ValidateLoadedWorkspace(session.ViewModel, "默认模板");
        session.ViewModel.StatusMessage = usable
            ? "模板加载成功，可直接启动轮询"
            : "模板加载成功，请先配置点位再启动轮询";
    }

    /// <summary>退出时把当前会话工作区另存为 last.mbwork，下次启动恢复。</summary>
    public async Task SaveLastAsync()
    {
        try
        {
            ModbusTool.App.ViewModels.MasterViewModel? viewModel = _current?.ViewModel
                ?? _sessions.FirstOrDefault()?.ViewModel;
            if (viewModel is null)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(Services.AppDataPathHelper.WorkspacesFolder);
            await viewModel.WorkspaceManager.WriteSnapshotAsync(Services.AppDataPathHelper.LastWorkspaceFile);
        }
        catch (Exception exception)
        {
            App.Log.Write(ModbusCore.Common.ToolLogLevel.Warning, "界面", "保存 last.mbwork 失败", exception);
        }
    }

    /// <summary>【重置为初始模板】：当前会话恢复出厂默认模板，不影响已保存的用户模板。</summary>
    public async Task ResetToBuiltInTemplateAsync()
    {
        MasterView? session = _current ?? _sessions.FirstOrDefault();
        if (session is null || !session.ViewModel.IsOffline)
        {
            if (session is not null)
            {
                MessageBox.Show("请先停止当前会话再重置。", "重置为初始模板", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        ModbusMaster.MasterWorkspace defaultWorkspace = await Services.DefaultTemplateService.LoadBuiltInAsync();
        session.ViewModel.LoadClonedWorkspace(defaultWorkspace);
        session.ViewModel.RenameWorkspace("默认模板");
        ValidateLoadedWorkspace(session.ViewModel, "重置默认模板");
        session.ViewModel.StatusMessage = "已重置为初始模板，可直接启动轮询";
    }

    public async Task OpenWorkspaceAsync(string filePath)
    {
        MasterView? session = _current ?? _sessions.FirstOrDefault();
        if (session is null)
        {
            return;
        }

        await session.ViewModel.OpenWorkspaceFileAsync(filePath);
    }

    public void DuplicateCurrentSession()
    {
        if (_current is null)
        {
            return;
        }

        MasterWorkspace clone = _current.ViewModel.CloneWorkspace();
        MasterView copy = CreateSession(clone);
        copy.ViewModel.RenameWorkspace($"{_current.ViewModel.Workspace.Name} 副本");
        App.Log.Write(ModbusCore.Common.ToolLogLevel.Information, "界面", $"已复制会话：{copy.ViewModel.Workspace.Name}");
    }

    public async Task CloseCurrentSessionAsync()
    {
        if (_current is null || _disposed)
        {
            return;
        }

        MasterView session = _current;
        _sessions.Remove(session);
        SessionViewModels.Remove(session.ViewModel);
        if (ReferenceEquals(SessionHost.Content, session))
        {
            SessionHost.Content = null;
        }

        _current = null;
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception)
        {
            App.Log.Write(ModbusCore.Common.ToolLogLevel.Warning, "界面", "关闭会话时清理失败", exception);
        }

        if (_sessions.Count == 0 && !_disposed)
        {
            CreateSession();
        }
        else
        {
            _current = _sessions[^1];
            SessionHost.Content = _current;
            RaiseSelectionChanged();
            CurrentViewChanged?.Invoke(_current);
        }
    }

    public void RenameCurrentSession(string name)
    {
        _current?.ViewModel.RenameWorkspace(name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SessionHost.Content = null;
        foreach (MasterView session in _sessions.ToArray())
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
        SessionViewModels.Clear();
        _current = null;
    }

    private MasterView CreateSession(MasterWorkspace? workspace = null)
    {
        MasterView view = new();
        if (workspace is not null)
        {
            view.ViewModel.LoadClonedWorkspace(workspace);
        }
        else
        {
            view.ViewModel.RenameWorkspace($"会话 {++_sessionCounter}");
        }

        string? portConflict = view.ViewModel.GetSerialPortConflictMessage();
        if (portConflict is not null)
        {
            MessageBox.Show(portConflict, "串口已被占用", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _sessions.Add(view);
        SessionViewModels.Add(view.ViewModel);
        _current = view;
        SessionHost.Content = view;
        RaiseSelectionChanged();
        CurrentViewChanged?.Invoke(view);
        return view;
    }

    private void RaiseSelectionChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedViewModel)));
}
