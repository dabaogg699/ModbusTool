// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using ModbusCore.ByteConvert;
using ModbusCore.Common;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;
using ModbusMaster;
using ModbusTool.App.Controls;
using ModbusTool.App.Services;

namespace ModbusTool.App.ViewModels;

public sealed class MasterViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly string TemplateDirectory = Services.AppDataPathHelper.TemplatesFolder;
    private readonly MasterWorkspaceManager _workspaceManager = new();

    /// <summary>工作区管理器，供启动恢复（last.mbwork）与退出快照使用；业务操作仍走 ViewModel 方法。</summary>
    public MasterWorkspaceManager WorkspaceManager => _workspaceManager;
    private readonly ConcurrentQueue<MasterResponseEventArgs> _responses = new();
    private readonly ConcurrentQueue<MasterErrorEventArgs> _errors = new();
    private readonly Dictionary<Guid, MasterBlockItem> _blockLookup = [];
    private readonly Dictionary<Guid, MasterPointItem> _pointLookup = [];
    private readonly Dictionary<Guid, DeviceHealthItem> _healthLookup = [];
    private string? _lastValidSerialPort;
    private readonly Dictionary<int, RegisterGridItem> _registerLookup = [];
    private readonly MasterWritePendingTracker _writePending = new();
    private readonly DispatcherTimer _uiTimer;
    private MasterService? _service;
    private TrendService _trendService;
    private MasterDevice? _selectedDevice;
    private MasterBlockItem? _selectedBlock;
    private MasterPointItem? _selectedPoint;
    private MasterWorkspace? _selectedLegacyWorkspace;
    private bool _isRunning;
    private string _statusMessage = "离线编辑模式";
    private string _lastManualResponse = "尚未执行";
    private string _statisticsText = "暂无通信统计";
    private ModbusFunction _manualFunction = ModbusFunction.ReadHoldingRegisters;
    private byte _manualUnitId = 1;
    private ushort _manualAddress;
    private ushort _manualQuantity = 1;
    private string _manualValuesText = "1";
    private string _baudRateText = "9600";
    private string _baudRateError = string.Empty;

    public MasterViewModel()
    {
        _trendService = new TrendService();
        _trendService.SampleRecorded += OnTrendSampleRecorded;

        NewCommand = new RelayCommand(_ => NewWorkspace(), _ => IsOffline);
        OpenCommand = new AsyncRelayCommand(_ => OpenWorkspaceAsync(), _ => IsOffline, HandleCommandError);
        ImportLegacyCommand = new AsyncRelayCommand(_ => OpenWorkspaceAsync(true), _ => IsOffline, HandleCommandError);
        SaveCommand = new AsyncRelayCommand(_ => SaveWorkspaceAsync(false), onError: HandleCommandError);
        SaveAsCommand = new AsyncRelayCommand(_ => SaveWorkspaceAsync(true), onError: HandleCommandError);
        SaveTemplateCommand = new AsyncRelayCommand(_ => SaveTemplateAsync(), onError: HandleCommandError);
        NewFromTemplateCommand = new AsyncRelayCommand(_ => NewFromTemplateAsync(), _ => IsOffline, HandleCommandError);
        AddDeviceCommand = new RelayCommand(_ => AddDevice(), _ => IsOffline);
        AddBlockCommand = new RelayCommand(_ => AddBlock(), _ => IsOffline && SelectedDevice is not null);
        AddPointCommand = new RelayCommand(_ => AddPoint(), _ => IsOffline && IsSelectedBlockRegister);
        RemoveCommand = new RelayCommand(RemoveSelected, CanRemoveSelected);
        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => IsOffline, HandleCommandError);
        StopCommand = new AsyncRelayCommand(_ => StopAsync(), _ => IsRunning, HandleCommandError);
        ManualExecuteCommand = new AsyncRelayCommand(_ => ExecuteManualAsync(), _ => IsRunning, HandleCommandError);
        RefreshPortsCommand = new RelayCommand(_ => RefreshPorts());
        ImportPointsCommand = new AsyncRelayCommand(_ => ImportPointsAsync(), _ => IsOffline && IsSelectedBlockRegister, HandleCommandError);
        ExportPointsCommand = new AsyncRelayCommand(_ => ExportPointsAsync(), onError: HandleCommandError);
        ExportRegistersCommand = new AsyncRelayCommand(_ => ExportRegistersAsync(), _ => SelectedBlock is not null, HandleCommandError);
        ExportHistoryCommand = new AsyncRelayCommand(_ => ExportHistoryAsync(), onError: HandleCommandError);
        ExportLegacyCsvCommand = new AsyncRelayCommand(_ => ExportLegacyCsvAsync(), onError: HandleCommandError);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTimerTick;
        _uiTimer.Start();
        RefreshPorts();
    }

    public event Action<Guid, TrendSample>? TrendSampleAvailable;

    public event Action? TrendSeriesChanged;

    public event Action<string>? ErrorOccurred;

    public ObservableCollection<MasterDevice> Devices { get; } = [];
    public ObservableCollection<MasterBlockItem> Blocks { get; } = [];
    public ObservableCollection<MasterPointItem> Points { get; } = [];
    public ObservableCollection<RegisterGridItem> Registers { get; } = [];
    public ObservableCollection<RegisterGridItem> ManualResults { get; } = [];
    public ObservableCollection<DeviceHealthItem> DeviceHealth { get; } = [];
    public ObservableCollection<PointGroupItem> PointGroups { get; } = [];
    public ObservableCollection<string> PortNames { get; } = [];

    public IReadOnlyList<ModbusFunctionOption> ReadFunctions { get; } =
    [
        new(ModbusFunction.ReadCoils, "01 读线圈"),
        new(ModbusFunction.ReadDiscreteInputs, "02 读离散输入"),
        new(ModbusFunction.ReadHoldingRegisters, "03 读保持寄存器"),
        new(ModbusFunction.ReadInputRegisters, "04 读输入寄存器")
    ];

    public IReadOnlyList<ModbusFunctionOption> ManualFunctions { get; } =
    [
        new(ModbusFunction.ReadCoils, "01 读线圈"),
        new(ModbusFunction.ReadDiscreteInputs, "02 读离散输入"),
        new(ModbusFunction.ReadHoldingRegisters, "03 读保持寄存器"),
        new(ModbusFunction.ReadInputRegisters, "04 读输入寄存器"),
        new(ModbusFunction.WriteSingleCoil, "05 写单线圈"),
        new(ModbusFunction.WriteSingleRegister, "06 写单寄存器"),
        new(ModbusFunction.WriteMultipleCoils, "0F 写多线圈"),
        new(ModbusFunction.WriteMultipleRegisters, "10 写多寄存器")
    ];

    public IReadOnlyList<ModbusDataType> DataTypes { get; } = Enum.GetValues<ModbusDataType>();
    public IReadOnlyList<ModbusByteOrder> ByteOrders { get; } = Enum.GetValues<ModbusByteOrder>();
    public IReadOnlyList<MasterConnectionKind> ConnectionKinds { get; } = Enum.GetValues<MasterConnectionKind>();
    public IReadOnlyList<string> Parities { get; } = Enum.GetNames<Parity>();
    public IReadOnlyList<string> StopBitNames { get; } = Enum.GetNames<StopBits>();
    public IReadOnlyList<int> BaudRates => SerialBaudRates.Presets;

    public string BaudRateText
    {
        get => _baudRateText;
        set
        {
            if (!SetProperty(ref _baudRateText, value))
            {
                return;
            }

            if (SerialBaudRates.TryValidate(value, out int baudRate, out string error))
            {
                Connection.BaudRate = baudRate;
            }

            BaudRateError = error;
        }
    }

    public string BaudRateError
    {
        get => _baudRateError;
        private set
        {
            if (SetProperty(ref _baudRateError, value))
            {
                OnPropertyChanged(nameof(HasBaudRateError));
            }
        }
    }

    public bool HasBaudRateError => BaudRateError.Length > 0;

    public RelayCommand NewCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand ImportLegacyCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAsCommand { get; }
    public AsyncRelayCommand SaveTemplateCommand { get; }
    public AsyncRelayCommand NewFromTemplateCommand { get; }
    public RelayCommand AddDeviceCommand { get; }
    public RelayCommand AddBlockCommand { get; }
    public RelayCommand AddPointCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ManualExecuteCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public AsyncRelayCommand ImportPointsCommand { get; }
    public AsyncRelayCommand ExportPointsCommand { get; }
    public AsyncRelayCommand ExportRegistersCommand { get; }
    public AsyncRelayCommand ExportHistoryCommand { get; }
    public AsyncRelayCommand ExportLegacyCsvCommand { get; }

    public MasterWorkspace Workspace => _workspaceManager.Current;
    public string SessionId { get; } = $"M-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>趋势曲线样式变更后标记工作区为已修改，确保保存时写入样式。</summary>
    public void NotifyTrendStyleChanged() => MarkDirty();
    public MasterConnectionSettings Connection => Workspace.Connection;
    public string WorkspaceDisplayName => $"{Workspace.Name}{(_workspaceManager.IsDirty ? " *" : string.Empty)}";
    public string? WorkspaceFilePath => _workspaceManager.FilePath;
    public IReadOnlyList<MasterWorkspace> ImportedWorkspaces => _workspaceManager.ImportedWorkspaces;
    public bool HasLegacyWorkspaces => ImportedWorkspaces.Count > 1;
    public bool IsTcpConnection => Connection.Kind == MasterConnectionKind.Tcp;
    public bool IsSelectedBlockRegister => SelectedBlock?.Function is ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters;
    public bool IsRegisterWriteEnabled => IsRunning && SelectedBlock?.Function is ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadCoils;
    public bool IsRegisterGridReadOnly => !IsRegisterWriteEnabled;
    public IEnumerable<MasterPointItem> HistoryPoints => _pointLookup.Values.Where(point => point.HistoryEnabled);

    public MasterDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RebuildBlocks();
            }
        }
    }

    public MasterBlockItem? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (SetProperty(ref _selectedBlock, value))
            {
                RebuildPoints();
                RebuildRegisters();
                OnPropertyChanged(nameof(IsSelectedBlockRegister));
                OnPropertyChanged(nameof(IsRegisterWriteEnabled));
                OnPropertyChanged(nameof(IsRegisterGridReadOnly));
            }
        }
    }

    public MasterPointItem? SelectedPoint { get => _selectedPoint; set => SetProperty(ref _selectedPoint, value); }

    public MasterWorkspace? SelectedLegacyWorkspace
    {
        get => _selectedLegacyWorkspace;
        set
        {
            if (!SetProperty(ref _selectedLegacyWorkspace, value) || value is null || ReferenceEquals(value, Workspace))
            {
                return;
            }

            _workspaceManager.ActivateImportedWorkspace(value);
            ReloadWorkspace();
            StatusMessage = $"已切换到旧工作区：{Workspace.Name}；保存时会写入新版 JSON 文件";
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsOffline));
                OnPropertyChanged(nameof(IsRegisterWriteEnabled));
                OnPropertyChanged(nameof(IsRegisterGridReadOnly));
            }
        }
    }

    public bool IsOffline => !IsRunning;
    public string StatusMessage { get => _statusMessage; internal set => SetProperty(ref _statusMessage, value); }
    public string LastManualResponse { get => _lastManualResponse; private set => SetProperty(ref _lastManualResponse, value); }
    public string StatisticsText { get => _statisticsText; private set => SetProperty(ref _statisticsText, value); }
    public ModbusFunction ManualFunction { get => _manualFunction; set => SetProperty(ref _manualFunction, value); }
    public byte ManualUnitId { get => _manualUnitId; set => SetProperty(ref _manualUnitId, value); }
    public ushort ManualAddress { get => _manualAddress; set => SetProperty(ref _manualAddress, value); }
    public ushort ManualQuantity { get => _manualQuantity; set => SetProperty(ref _manualQuantity, value); }
    public string ManualValuesText { get => _manualValuesText; set => SetProperty(ref _manualValuesText, value); }

    public void NotifyConfigurationEdited(string section)
    {
        MarkDirty();
        if (section == "Connection")
        {
            ValidateSerialPortLease();
            OnPropertyChanged(nameof(Connection));
            OnPropertyChanged(nameof(IsTcpConnection));
        }
        else if (section == "Device")
        {
            foreach (MasterDevice device in Workspace.Devices)
            {
                if (_healthLookup.TryGetValue(device.Id, out DeviceHealthItem? health))
                {
                    health.Name = device.Name;
                    health.UnitId = device.UnitId;
                }
            }
        }
        else if (section == "Block")
        {
            OnPropertyChanged(nameof(IsSelectedBlockRegister));
            RebuildPoints();
            RebuildRegisters();
        }
        else if (section == "Point")
        {
            RegroupPoints();
        }

        TrendSeriesChanged?.Invoke();
    }

    public IReadOnlyList<TrendSample> GetTrendSnapshot(Guid pointId) => _trendService.GetSnapshot(pointId);

    public async Task OpenWorkspaceFileAsync(string filePath)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("请先停止当前主站会话，再打开工作区。");
        }

        await _workspaceManager.LoadAsync(filePath).ConfigureAwait(true);
        ReloadWorkspace();
        StatusMessage = _workspaceManager.ImportedLegacyConfiguration is not null
            ? $"已迁移 {ImportedWorkspaces.Count} 个旧工作区；原 config.txt 不会被覆盖"
            : $"已打开 {WorkspaceFilePath}";
        App.Log.Write(ToolLogLevel.Information, "主站.操作", StatusMessage);
    }

    public MasterWorkspace CloneWorkspace() => _workspaceManager.CloneCurrent();

    public void LoadClonedWorkspace(MasterWorkspace workspace)
    {
        if (IsRunning)
        {
            return;
        }

        _workspaceManager.LoadModel(workspace);
        ReloadWorkspace();
        StatusMessage = $"已复制会话：{Workspace.Name}";
        App.Log.Write(ToolLogLevel.Information, "主站.操作", StatusMessage);
    }

    public void RenameWorkspace(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Workspace.Name = name.Trim();
        MarkDirty();
        OnPropertyChanged(nameof(WorkspaceDisplayName));
        StatusMessage = $"会话已重命名为：{Workspace.Name}";
    }

    private void ValidateSerialPortLease()
    {
        if (Connection.Kind != MasterConnectionKind.SerialRtu)
        {
            _lastValidSerialPort = Connection.PortName;
            return;
        }

        string portName = Connection.PortName?.Trim() ?? string.Empty;
        string? owner = SerialPortLeaseRegistry.GetOwnerSession(portName);
        if (owner is not null && !SerialPortLeaseRegistry.IsOwnedBy(portName, this))
        {
            string conflictingPort = portName;
            if (!string.IsNullOrWhiteSpace(_lastValidSerialPort))
            {
                Connection.PortName = _lastValidSerialPort;
            }

            string message = $"串口 {conflictingPort} 已被会话“{owner}”占用，已恢复为 {Connection.PortName}。";
            StatusMessage = message;
            ErrorOccurred?.Invoke(message);
            return;
        }

        _lastValidSerialPort = portName;
    }

    public string? GetSerialPortConflictMessage()
    {
        if (Connection.Kind != MasterConnectionKind.SerialRtu)
        {
            return null;
        }

        string? owner = SerialPortLeaseRegistry.GetOwnerSession(Connection.PortName);
        return owner is null
            ? null
            : $"当前会话串口 {Connection.PortName} 已被会话“{owner}”占用，启动前请修改串口。";
    }

    public void BatchAddPoints(int count = 10)
    {
        if (!IsOffline || SelectedBlock is null || !IsSelectedBlockRegister)
        {
            return;
        }

        for (int index = 0; index < count; index++)
        {
            AddPoint();
        }

        StatusMessage = $"已批量添加 {count} 个点位";
    }

    public void DuplicateSelectedPoint()
    {
        if (!IsOffline || SelectedBlock is null || SelectedPoint is null)
        {
            return;
        }

        MasterDataPoint source = SelectedPoint.Model;
        MasterDataPoint clone = new()
        {
            Name = $"{source.Name} 副本",
            AddressOffset = FindFreePointOffset(SelectedBlock.Model),
            DataType = source.DataType,
            ByteOrder = source.ByteOrder,
            Scale = source.Scale,
            Offset = source.Offset,
            Unit = source.Unit,
            HistoryEnabled = source.HistoryEnabled,
            GroupName = source.GroupName,
            Note = source.Note
        };
        MasterPointItem item = new(clone);
        SelectedBlock.Model.Points.Add(clone);
        Points.Add(item);
        _pointLookup[clone.Id] = item;
        SelectedPoint = item;
        RegroupPoints();
        MarkDirty();
        TrendSeriesChanged?.Invoke();
        StatusMessage = $"已复制点位：{clone.Name}";
    }

    public void SetSelectedPointDataType(ModbusDataType dataType)
    {
        if (!IsOffline || SelectedPoint is null)
        {
            return;
        }

        SelectedPoint.DataType = dataType;
        int count = RegisterConverter.GetRegisterCount(dataType);
        if (SelectedBlock is not null
            && SelectedPoint.AddressOffset + count > SelectedBlock.Quantity)
        {
            SelectedPoint.AddressOffset = (ushort)Math.Max(0, SelectedBlock.Quantity - count);
        }

        MarkDirty();
        StatusMessage = $"点位 {SelectedPoint.Name} 数据类型已改为 {dataType}";
    }

    public void SetSelectedPointByteOrder(ModbusByteOrder byteOrder)
    {
        if (!IsOffline || SelectedPoint is null)
        {
            return;
        }

        SelectedPoint.ByteOrder = byteOrder;
        MarkDirty();
        StatusMessage = $"点位 {SelectedPoint.Name} 字节序已改为 {byteOrder}";
    }

    public async Task ExportSelectedPointsAsync()
    {
        if (SelectedPoint is null)
        {
            return;
        }

        SaveFileDialog dialog = CreateCsvDialog("导出选中点位", "MasterPoint");
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        MasterPointItem point = SelectedPoint;
        MasterReadBlock block = SelectedBlock!.Model;
        string unitId = SelectedDevice?.UnitId.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        await CsvService.WriteAsync(dialog.FileName,
            ["Device", "UnitId", "Block", "Function", "BlockStart", "Name", "AddressOffset", "DataType", "ByteOrder", "Scale", "Offset", "Unit", "HistoryEnabled", "GroupName", "Note"],
            [(IEnumerable<string?>)[
                SelectedDevice?.Name ?? string.Empty, unitId, block.Name,
                ((byte)block.Function).ToString("X2"), block.StartAddress.ToString(CultureInfo.InvariantCulture),
                point.Name, point.AddressOffset.ToString(CultureInfo.InvariantCulture),
                point.DataType.ToString(), point.ByteOrder.ToString(),
                point.Scale.ToString("R", CultureInfo.InvariantCulture), point.Offset.ToString("R", CultureInfo.InvariantCulture),
                point.Unit, point.HistoryEnabled.ToString(), point.GroupName, point.Note
            ]]).ConfigureAwait(true);
        StatusMessage = $"选中点位已导出：{dialog.FileName}";
    }

    public async Task ExportLegacyCsvAsync()
    {
        MessageBoxResult confirmation = MessageBox.Show(
            $"{LegacyMbscanCsvExporter.CompatibilityWarning}。\n\n原版样本到位前请勿作为正式迁移依据。",
            "旧版 MBScan CSV（推断结构）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SaveFileDialog dialog = CreateCsvDialog("导出旧版 MBScan CSV（推断结构）", "LegacyMbscan");
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        List<LegacyMbscanRow> rows = [];
        foreach (MasterDevice device in Workspace.Devices)
        {
            foreach (MasterReadBlock block in device.ReadBlocks)
            {
                foreach (MasterDataPoint point in block.Points)
                {
                    rows.Add(new LegacyMbscanRow(
                        device.UnitId,
                        block.StartAddress,
                        (ushort)RegisterConverter.GetRegisterCount(point.DataType),
                        block.Function,
                        point.Name,
                        point.DataType,
                        point.ByteOrder.ToString(),
                        point.Scale,
                        point.Offset,
                        point.Unit,
                        point.Note));
                }
            }
        }

        await LegacyMbscanCsvExporter.ExportAsync(dialog.FileName, rows).ConfigureAwait(true);
        StatusMessage = $"旧版 MBScan CSV（推断结构）已导出：{dialog.FileName}；{LegacyMbscanCsvExporter.CompatibilityWarning}";
    }

    /// <summary>
    /// 写入单个线圈或保持寄存器。用户输入先作为预览值显示并锁定该地址，
    /// 写应答由 <see cref="ProcessWriteResponse"/> 按接收顺序解除锁定并回填真实值。
    /// </summary>
    public async Task WriteRegisterValueAsync(RegisterGridItem item, string text)
    {
        if (!IsRunning || _service is null || SelectedBlock is null || SelectedDevice is null)
        {
            throw new InvalidOperationException("请先启动主站并选中一个读取块。");
        }

        int offset = item.Address - SelectedBlock.StartAddress;
        if (offset < 0 || offset >= SelectedBlock.Quantity)
        {
            throw new InvalidOperationException("该地址不属于当前读取块。");
        }

        ModbusRequest request;
        if (SelectedBlock.Function == ModbusFunction.ReadCoils)
        {
            request = ModbusRequest.CreateWriteSingleCoil((ushort)item.Address, ParseBit(text));
        }
        else if (SelectedBlock.Function == ModbusFunction.ReadHoldingRegisters)
        {
            request = ModbusRequest.CreateWriteSingleRegister((ushort)item.Address, ParseUInt16(text));
        }
        else
        {
            throw new InvalidOperationException("输入寄存器与离散输入为只读，请切换到保持寄存器或线圈读取块。");
        }

        byte unitId = SelectedDevice.UnitId;
        ushort address = (ushort)item.Address;
        if (!_writePending.TryBegin(unitId, address))
        {
            throw new InvalidOperationException($"地址 {item.Address} 正在等待上一次写入应答，请稍后重试。");
        }

        SetRegisterPreview(item, request);
        App.Log.Write(ToolLogLevel.Information, "主站.写入",
            $"写入请求已下发：站号 {unitId}，地址 {address}，目标值 {item.Value}", (string?)null,
            context: RequestContext(unitId, request));
        try
        {
            ModbusResponse response = await _service.ExecuteAsync(unitId, request).ConfigureAwait(true);
            if (response.IsException)
            {
                throw new InvalidOperationException(
                    $"写入失败：从站返回异常 0x{(byte)response.ExceptionCode!.Value:X2}（{response.ExceptionCode}）。");
            }
        }
        catch (Exception exception)
        {
            // 正常应答由响应队列统一完成解锁；异常或超时在此立即解锁并给出明确反馈。
            if (_writePending.Complete(unitId, address))
            {
                item.Status = "写入失败";
                item.State = GridValueState.Error;
                StatusMessage = $"写入失败：地址 {address}，{exception.Message}";
                App.Log.Write(ToolLogLevel.Error, "主站.写入", StatusMessage, exception,
                    context: RequestContext(unitId, request));
            }

            if (exception.Message.StartsWith("写入失败：", StringComparison.Ordinal))
            {
                throw;
            }

            throw new InvalidOperationException($"写入失败：地址 {address}，{exception.Message}", exception);
        }
    }

    private static void SetRegisterPreview(RegisterGridItem item, ModbusRequest request)
    {
        SetRegisterValue(item, request.Function, request.Value);
        item.Status = "写入中";
        item.State = GridValueState.WritePending;
    }

    private static void SetRegisterValue(RegisterGridItem item, ModbusFunction function, ushort value)
    {
        if (function == ModbusFunction.WriteSingleCoil)
        {
            bool bitValue = value == 0xFF00;
            item.Value = bitValue ? "1 (ON)" : "0 (OFF)";
            item.HexValue = bitValue ? "0x01" : "0x00";
            item.BinaryValue = RegisterGridItem.FormatBinary(bitValue);
            return;
        }

        item.Value = value.ToString(CultureInfo.InvariantCulture);
        item.HexValue = $"0x{value:X4}";
        item.BinaryValue = RegisterGridItem.FormatBinary(value);
        item.TextValue = RegisterToAscii(value);
    }

    public async Task WriteSelectedPointValueAsync(double engineeringValue)
    {
        if (!IsRunning || _service is null || SelectedPoint is null || SelectedBlock is null || SelectedDevice is null)
        {
            throw new InvalidOperationException("请先启动主站并选中一个数值点位。");
        }

        double rawValue = SelectedPoint.Scale == 0
            ? engineeringValue
            : (engineeringValue - SelectedPoint.Offset) / SelectedPoint.Scale;
        ushort[] registers = RegisterConverter.Encode(rawValue, SelectedPoint.DataType, SelectedPoint.ByteOrder);
        int address = SelectedBlock.StartAddress + SelectedPoint.AddressOffset;
        ModbusRequest request = registers.Length == 1
            ? ModbusRequest.CreateWriteSingleRegister((ushort)address, registers[0])
            : ModbusRequest.CreateWriteMultipleRegisters((ushort)address, registers);
        ModbusResponse response = await _service.ExecuteAsync(SelectedDevice.UnitId, request).ConfigureAwait(true);
        if (response.IsException)
        {
            throw new InvalidOperationException($"从站返回异常 0x{(byte)response.ExceptionCode!.Value:X2}（{response.ExceptionCode}）。");
        }

        StatusMessage = $"已写入 {SelectedPoint.Name} = {engineeringValue:G8}（地址 {address}）";
        App.Log.Write(ToolLogLevel.Information, "主站.写入", StatusMessage, (string?)null,
            context: RequestContext(SelectedDevice!.UnitId, request));
    }

    public void SetAllDevicesEnabled(bool enabled)
    {
        if (!IsOffline)
        {
            return;
        }

        foreach (MasterDevice device in Workspace.Devices)
        {
            device.Enabled = enabled;
        }

        CollectionViewSource.GetDefaultView(Devices).Refresh();
        MarkDirty();
        StatusMessage = enabled ? "已批量启用全部从站轮询" : "已批量禁用全部从站轮询";
    }

    public void SetSelectedDeviceInterval(int intervalMs)
    {
        if (!IsOffline || SelectedDevice is null || intervalMs < 1)
        {
            return;
        }

        SelectedDevice.PollIntervalMs = intervalMs;
        CollectionViewSource.GetDefaultView(Devices).Refresh();
        MarkDirty();
        StatusMessage = $"从站 {SelectedDevice.Name} 轮询周期已改为 {intervalMs} ms";
    }

    public void MoveSelectedDevice(int offset)
    {
        if (!IsOffline || SelectedDevice is null)
        {
            return;
        }

        int index = Workspace.Devices.IndexOf(SelectedDevice);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= Workspace.Devices.Count)
        {
            return;
        }

        Workspace.Devices.RemoveAt(index);
        Workspace.Devices.Insert(target, SelectedDevice);
        int viewIndex = Devices.IndexOf(SelectedDevice);
        if (viewIndex >= 0)
        {
            Devices.Move(viewIndex, target);
        }

        MarkDirty();
    }

    public void MoveDevice(int fromIndex, int toIndex)
    {
        if (!IsOffline || fromIndex < 0 || fromIndex >= Devices.Count)
        {
            return;
        }

        toIndex = Math.Clamp(toIndex, 0, Devices.Count - 1);
        if (fromIndex == toIndex)
        {
            return;
        }

        MasterDevice device = Devices[fromIndex];
        MasterDevice? selected = SelectedDevice;
        Devices.RemoveAt(fromIndex);
        Devices.Insert(toIndex, device);
        Workspace.Devices.Clear();
        foreach (MasterDevice item in Devices)
        {
            Workspace.Devices.Add(item);
        }

        SelectedDevice = selected;
        MarkDirty();
        StatusMessage = $"已调整从站轮询顺序：{device.Name} → 第 {toIndex + 1} 位（保存后按此顺序下发轮询）";
    }

    public async ValueTask DisposeAsync()
    {
        _uiTimer.Stop();
        await DisposeServiceAsync().ConfigureAwait(false);
        _trendService.SampleRecorded -= OnTrendSampleRecorded;
    }

    private void NewWorkspace()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        _workspaceManager.New();
        ReloadWorkspace();
        StatusMessage = "已新建工作区，可离线编辑";
        App.Log.Write(ToolLogLevel.Information, "主站.操作", StatusMessage);
    }

    private async Task OpenWorkspaceAsync(bool legacyOnly = false)
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = legacyOnly ? "导入旧版 MBScan config.txt" : "打开主站工作区",
            Filter = legacyOnly
                ? "旧版配置 (config.txt)|config.txt|所有文件 (*.*)|*.*"
                : "Modbus 工作区 (*.json)|*.json|旧版配置 (config.txt)|config.txt|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _workspaceManager.LoadAsync(dialog.FileName).ConfigureAwait(true);
        ReloadWorkspace();
        StatusMessage = _workspaceManager.ImportedLegacyConfiguration is not null
            ? $"已迁移 {ImportedWorkspaces.Count} 个旧工作区；原 config.txt 不会被覆盖，请保存为新版 JSON"
            : $"已打开 {WorkspaceFilePath}";
        App.Log.Write(ToolLogLevel.Information, "主站.操作", StatusMessage);
    }

    private async Task SaveWorkspaceAsync(bool saveAs)
    {
        string? filePath = _workspaceManager.FilePath;
        if (saveAs || string.IsNullOrWhiteSpace(filePath))
        {
            SaveFileDialog dialog = new()
            {
                Title = "保存主站工作区",
                Filter = "Modbus 工作区 (*.json)|*.json",
                FileName = $"{Workspace.Name}.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            filePath = dialog.FileName;
        }

        await _workspaceManager.SaveAsync(filePath).ConfigureAwait(true);
        NotifyWorkspaceProperties();
        StatusMessage = $"工作区已保存：{WorkspaceFilePath}";
        App.Log.Write(ToolLogLevel.Information, "主站.操作", StatusMessage);
    }

    private async Task SaveTemplateAsync()
    {
        Directory.CreateDirectory(TemplateDirectory);
        SaveFileDialog dialog = new()
        {
            Title = "另存为工作区模板",
            Filter = "Modbus 工作区模板 (*.json)|*.json",
            FileName = $"{Workspace.Name}-模板.json",
            InitialDirectory = TemplateDirectory,
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _workspaceManager.WriteSnapshotAsync(dialog.FileName).ConfigureAwait(true);
        StatusMessage = $"工作区模板已保存：{dialog.FileName}";
        App.Log.Write(ToolLogLevel.Information, "主站.操作", StatusMessage);
    }

    private async Task NewFromTemplateAsync()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        Directory.CreateDirectory(TemplateDirectory);
        OpenFileDialog dialog = new()
        {
            Title = "从模板新建工作区",
            Filter = "Modbus 工作区模板 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = TemplateDirectory
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _workspaceManager.LoadAsync(dialog.FileName).ConfigureAwait(true);
        _workspaceManager.LoadModel(_workspaceManager.Current);
        ReloadWorkspace();
        StatusMessage = $"已从模板新建会话：{Workspace.Name}；保存时另选工作区文件";
        App.Log.Write(ToolLogLevel.Information, "主站.操作", StatusMessage);
    }

    private bool ConfirmDiscardChanges()    {
        return !_workspaceManager.IsDirty || MessageBox.Show(
            "当前工作区有未保存更改，继续将丢失这些更改。是否继续？",
            "未保存的工作区",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ReloadWorkspace()
    {
        _lastValidSerialPort = Workspace.Connection.PortName;
        _baudRateText = Workspace.Connection.BaudRate.ToString(CultureInfo.InvariantCulture);
        BaudRateError = string.Empty;
        OnPropertyChanged(nameof(BaudRateText));
        Devices.Clear();
        Blocks.Clear();
        Points.Clear();
        Registers.Clear();
        DeviceHealth.Clear();
        _blockLookup.Clear();
        _pointLookup.Clear();
        _healthLookup.Clear();
        foreach (MasterDevice device in Workspace.Devices)
        {
            Devices.Add(device);
            DeviceHealthItem health = new()
            {
                DeviceId = device.Id,
                Name = device.Name,
                UnitId = device.UnitId
            };
            _healthLookup[device.Id] = health;
            DeviceHealth.Add(health);
            foreach (MasterReadBlock block in device.ReadBlocks)
            {
                MasterBlockItem blockItem = new(block);
                _blockLookup[block.Id] = blockItem;
                foreach (MasterDataPoint point in block.Points)
                {
                    _pointLookup[point.Id] = new MasterPointItem(point);
                }
            }
        }

        SelectedDevice = Devices.FirstOrDefault();
        SelectedBlock = Blocks.FirstOrDefault();
        ResetTrendService();
        _selectedLegacyWorkspace = ImportedWorkspaces.Contains(Workspace) ? Workspace : null;
        OnPropertyChanged(nameof(SelectedLegacyWorkspace));
        NotifyWorkspaceProperties();
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(IsTcpConnection));
    }

    private void NotifyWorkspaceProperties()
    {
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(WorkspaceDisplayName));
        OnPropertyChanged(nameof(WorkspaceFilePath));
        OnPropertyChanged(nameof(ImportedWorkspaces));
        OnPropertyChanged(nameof(HasLegacyWorkspaces));
    }

    private void MarkDirty()
    {
        _workspaceManager.MarkDirty();
        NotifyWorkspaceProperties();
    }

    private void AddDevice()
    {
        byte nextUnitId = 1;
        while (Workspace.Devices.Any(device => device.UnitId == nextUnitId) && nextUnitId < byte.MaxValue)
        {
            nextUnitId++;
        }

        MasterDevice device = new() { Name = $"从站 {nextUnitId}", UnitId = nextUnitId };
        Workspace.Devices.Add(device);
        Devices.Add(device);
        DeviceHealthItem health = new()
        {
            DeviceId = device.Id,
            Name = device.Name,
            UnitId = device.UnitId
        };
        _healthLookup[device.Id] = health;
        DeviceHealth.Add(health);
        SelectedDevice = device;
        AddBlock();
        MarkDirty();
    }

    private void AddBlock()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        MasterReadBlock model = new() { Name = $"读取块 {SelectedDevice.ReadBlocks.Count + 1}", Quantity = 10 };
        MasterBlockItem block = new(model);
        SelectedDevice.ReadBlocks.Add(model);
        Blocks.Add(block);
        _blockLookup[model.Id] = block;
        SelectedBlock = block;
        MarkDirty();
    }

    private void AddPoint(string? groupName = null)
    {
        if (SelectedBlock is null || !IsSelectedBlockRegister)
        {
            return;
        }

        MasterDataPoint model = new()
        {
            Name = $"点位 {SelectedBlock.Model.Points.Count + 1}",
            AddressOffset = FindFreePointOffset(SelectedBlock.Model),
            GroupName = NormalizeGroupName(groupName)
        };
        MasterPointItem point = new(model);
        SelectedBlock.Model.Points.Add(model);
        Points.Add(point);
        _pointLookup[model.Id] = point;
        SelectedPoint = point;
        RegroupPoints();
        MarkDirty();
        TrendSeriesChanged?.Invoke();
    }

    private static ushort FindFreePointOffset(MasterReadBlock block)
    {
        int offset = 0;
        foreach (MasterDataPoint point in block.Points.OrderBy(point => point.AddressOffset))
        {
            int count = RegisterConverter.GetRegisterCount(point.DataType);
            if (offset < point.AddressOffset)
            {
                break;
            }

            offset = point.AddressOffset + count;
        }

        return (ushort)Math.Min(offset, Math.Max(0, block.Quantity - 1));
    }

    private void RemoveSelected(object? parameter)
    {
        switch (parameter as string)
        {
            case "Device" when SelectedDevice is not null:
                MasterDevice device = SelectedDevice;
                foreach (MasterReadBlock block in device.ReadBlocks)
                {
                    _blockLookup.Remove(block.Id);
                    foreach (MasterDataPoint point in block.Points)
                    {
                        _pointLookup.Remove(point.Id);
                    }
                }

                Workspace.Devices.Remove(device);
                Devices.Remove(device);
                if (_healthLookup.Remove(device.Id, out DeviceHealthItem? health))
                {
                    DeviceHealth.Remove(health);
                }

                SelectedDevice = Devices.FirstOrDefault();
                break;
            case "Block" when SelectedDevice is not null && SelectedBlock is not null:
                MasterBlockItem blockItem = SelectedBlock;
                SelectedDevice.ReadBlocks.Remove(blockItem.Model);
                Blocks.Remove(blockItem);
                _blockLookup.Remove(blockItem.Id);
                foreach (MasterDataPoint point in blockItem.Model.Points)
                {
                    _pointLookup.Remove(point.Id);
                }

                SelectedBlock = Blocks.FirstOrDefault();
                break;
            case "Point" when SelectedBlock is not null && SelectedPoint is not null:
                MasterPointItem pointItem = SelectedPoint;
                SelectedBlock.Model.Points.Remove(pointItem.Model);
                Points.Remove(pointItem);
                _pointLookup.Remove(pointItem.Id);
                SelectedPoint = Points.FirstOrDefault();
                RegroupPoints();
                break;
            default:
                return;
        }

        MarkDirty();
        TrendSeriesChanged?.Invoke();
    }

    private bool CanRemoveSelected(object? parameter)
    {
        if (!IsOffline)
        {
            return false;
        }

        return (parameter as string) switch
        {
            "Device" => SelectedDevice is not null,
            "Block" => SelectedBlock is not null,
            "Point" => SelectedPoint is not null,
            _ => false
        };
    }

    private void RebuildBlocks()
    {
        Blocks.Clear();
        if (SelectedDevice is not null)
        {
            foreach (MasterReadBlock model in SelectedDevice.ReadBlocks)
            {
                if (!_blockLookup.TryGetValue(model.Id, out MasterBlockItem? block))
                {
                    block = new MasterBlockItem(model);
                    _blockLookup[model.Id] = block;
                }

                Blocks.Add(block);
            }
        }

        SelectedBlock = Blocks.FirstOrDefault();
    }

    private void RebuildPoints()
    {
        Points.Clear();
        if (SelectedBlock is not null)
        {
            foreach (MasterDataPoint model in SelectedBlock.Model.Points)
            {
                if (!_pointLookup.TryGetValue(model.Id, out MasterPointItem? point))
                {
                    point = new MasterPointItem(model);
                    _pointLookup[model.Id] = point;
                }

                Points.Add(point);
            }
        }

        SelectedPoint = Points.FirstOrDefault();
        RegroupPoints();
    }

    private void RegroupPoints()
    {
        if (Workspace.PointGroups.Count == 0)
        {
            Workspace.PointGroups.Add(new MasterPointGroup { Name = "默认分组", IsExpanded = true });
        }

        foreach (MasterPointItem point in Points)
        {
            string groupName = NormalizeGroupName(point.GroupName);
            point.Model.GroupName = groupName;
            if (!Workspace.PointGroups.Any(group => string.Equals(group.Name, groupName, StringComparison.Ordinal)))
            {
                Workspace.PointGroups.Add(new MasterPointGroup { Name = groupName, IsExpanded = true });
            }
        }

        Dictionary<string, PointGroupItem> existing = PointGroups.ToDictionary(group => group.Name);
        PointGroups.Clear();
        foreach (MasterPointGroup model in Workspace.PointGroups)
        {
            if (!existing.TryGetValue(model.Name, out PointGroupItem? item))
            {
                item = new PointGroupItem(model, OnGroupExpandedChanged);
            }

            item.Points.Clear();
            PointGroups.Add(item);
        }

        foreach (MasterPointItem point in Points)
        {
            PointGroupItem? group = PointGroups.FirstOrDefault(item =>
                string.Equals(item.Name, NormalizeGroupName(point.GroupName), StringComparison.Ordinal));
            group?.Points.Add(point);
        }

        foreach (PointGroupItem group in PointGroups)
        {
            group.NotifyCountChanged();
        }
    }

    private void OnGroupExpandedChanged(PointGroupItem group, bool expanded)
    {
        MarkDirty();
    }

    private static string NormalizeGroupName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "默认分组" : name.Trim();

    public void AddPointGroup(string name)
    {
        if (!IsOffline || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string groupName = name.Trim();
        if (Workspace.PointGroups.Any(group => string.Equals(group.Name, groupName, StringComparison.Ordinal)))
        {
            StatusMessage = $"分组“{groupName}”已存在";
            return;
        }

        Workspace.PointGroups.Add(new MasterPointGroup { Name = groupName, IsExpanded = true });
        RegroupPoints();
        MarkDirty();
        StatusMessage = $"已新增分组：{groupName}";
    }

    public void SetGroupHistory(PointGroupItem group, bool enabled)
    {
        if (!IsOffline)
        {
            return;
        }

        foreach (MasterPointItem point in group.Points)
        {
            point.HistoryEnabled = enabled;
        }

        MarkDirty();
        TrendSeriesChanged?.Invoke();
        StatusMessage = enabled
            ? $"分组“{group.Name}”已全部启用历史曲线"
            : $"分组“{group.Name}”已全部取消历史曲线";
    }

    public void AddPointToGroup(PointGroupItem group)
    {
        AddPoint(group.Name);
    }

    public void RemoveGroupPoints(PointGroupItem group)
    {
        if (!IsOffline || SelectedBlock is null || group.Points.Count == 0)
        {
            return;
        }

        foreach (MasterPointItem point in group.Points.ToArray())
        {
            SelectedBlock.Model.Points.Remove(point.Model);
            Points.Remove(point);
            _pointLookup.Remove(point.Id);
        }

        int removed = group.Points.Count;
        SelectedPoint = Points.FirstOrDefault();
        RegroupPoints();
        MarkDirty();
        TrendSeriesChanged?.Invoke();
        StatusMessage = $"已删除分组“{group.Name}”内的 {removed} 个点位";
    }

    private void RebuildRegisters()
    {
        Registers.Clear();
        _registerLookup.Clear();
        if (SelectedBlock is null)
        {
            return;
        }

        bool isBits = SelectedBlock.Function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs;
        for (int offset = 0; offset < SelectedBlock.Quantity; offset++)
        {
            int address = SelectedBlock.StartAddress + offset;
            RegisterGridItem row = new()
            {
                Address = address,
                Value = "-",
                HexValue = "-",
                BinaryValue = "-",
                TextValue = isBits ? "位值" : string.Empty,
                Status = "未更新"
            };
            Registers.Add(row);
            _registerLookup[address] = row;
        }
    }

    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        int serialBaudRate = 0;
        if (Connection.Kind == MasterConnectionKind.SerialRtu
            && !SerialBaudRates.TryValidate(BaudRateText, out serialBaudRate, out string baudRateError))
        {
            BaudRateError = baudRateError;
            throw new InvalidDataException(baudRateError);
        }

        IReadOnlyList<PollSchedule> schedules = _workspaceManager.BuildPollSchedules();
        MasterServiceOptions options = new()
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(Connection.ResponseTimeoutMs),
            RetryCount = Connection.RetryCount,
            RetryDelay = TimeSpan.FromMilliseconds(Connection.RetryDelayMs),
            ReconnectDelay = TimeSpan.FromMilliseconds(Connection.ReconnectDelayMs),
            FrameInterval = TimeSpan.FromMilliseconds(Connection.FrameIntervalMs),
            QueueCapacity = Connection.QueueCapacity
        };

        MasterService service;
        if (Connection.Kind == MasterConnectionKind.SerialRtu)
        {
            if (!Enum.TryParse(Connection.Parity, out Parity parity) || !Enum.TryParse(Connection.StopBits, out StopBits stopBits))
            {
                throw new InvalidDataException("串口校验位或停止位配置无效。");
            }

            SerialTransport transport = new(new SerialTransportSettings
            {
                PortName = Connection.PortName,
                BaudRate = serialBaudRate,
                DataBits = Connection.DataBits,
                Parity = parity,
                StopBits = stopBits
            }, ModbusRtuFrameKind.Response);
            service = new MasterService(transport, new RtuMasterFrameCodec(), token => transport.OpenAsync(token), options, _trendService);
        }
        else
        {
            TcpTransport transport = new();
            service = new MasterService(
                transport,
                new TcpMasterFrameCodec(),
                token => transport.ConnectAsync(Connection.Host, Connection.Port, token),
                options,
                _trendService);
        }

        _service = service;
        service.FrameSent += OnFrameSent;
        service.FrameReceived += OnFrameReceived;
        service.ResponseReceived += OnResponseReceived;
        service.CommandFailed += OnCommandFailed;
        try
        {
            if (Connection.Kind == MasterConnectionKind.SerialRtu)
            {
                SerialPortLeaseRegistry.Acquire(Connection.PortName, this, Workspace.Name);
            }

            service.ConfigurePolling(schedules);
            await service.StartAsync().ConfigureAwait(true);
            IsRunning = true;
            LastManualResponse = "服务已启动，可执行手动命令";
            StatusMessage = $"主站已启动：{schedules.Count} 个轮询块；手动命令优先";
            App.Log.Write(ToolLogLevel.Information, "主站.通信", StatusMessage, (string?)null, context: SessionContext());
        }
        catch
        {
            await DisposeServiceAsync().ConfigureAwait(true);
            throw;
        }
    }

    private async Task StopAsync()
    {
        await DisposeServiceAsync().ConfigureAwait(true);
        IsRunning = false;
        StatisticsText = "主站已停止";
        StatusMessage = "已停止通信，返回离线编辑模式";
        App.Log.Write(ToolLogLevel.Information, "主站.通信", StatusMessage, (string?)null, context: SessionContext());
    }

    private async ValueTask DisposeServiceAsync()
    {
        SerialPortLeaseRegistry.Release(this);
        MasterService? service = Interlocked.Exchange(ref _service, null);
        if (service is null)
        {
            return;
        }

        service.FrameSent -= OnFrameSent;
        service.FrameReceived -= OnFrameReceived;
        service.ResponseReceived -= OnResponseReceived;
        service.CommandFailed -= OnCommandFailed;
        await service.DisposeAsync().ConfigureAwait(false);
        _writePending.Clear();
    }

    private async Task ExecuteManualAsync()
    {
        if (_service is null || !IsRunning)
        {
            return;
        }

        ModbusRequest request = BuildManualRequest();
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ModbusResponse response = await _service.ExecuteAsync(ManualUnitId, request).ConfigureAwait(true);
        stopwatch.Stop();
        UpdateManualResults(request, response, stopwatch.Elapsed);
    }

    private ModbusRequest BuildManualRequest()
    {
        string[] values = SplitValues(ManualValuesText);
        return ManualFunction switch
        {
            ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs
                or ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters
                => ModbusRequest.CreateRead(ManualFunction, ManualAddress, ManualQuantity),
            ModbusFunction.WriteSingleCoil
                => ModbusRequest.CreateWriteSingleCoil(ManualAddress, ParseBit(values.FirstOrDefault() ?? "0")),
            ModbusFunction.WriteSingleRegister
                => ModbusRequest.CreateWriteSingleRegister(ManualAddress, ParseUInt16(values.FirstOrDefault() ?? "0")),
            ModbusFunction.WriteMultipleCoils
                => ModbusRequest.CreateWriteMultipleCoils(ManualAddress, values.Select(ParseBit).ToArray()),
            ModbusFunction.WriteMultipleRegisters
                => ModbusRequest.CreateWriteMultipleRegisters(ManualAddress, values.Select(ParseUInt16).ToArray()),
            _ => throw new InvalidDataException("请选择有效的手动功能码。")
        };
    }

    private void UpdateManualResults(ModbusRequest request, ModbusResponse response, TimeSpan elapsed)
    {
        ManualResults.Clear();
        if (response.IsException)
        {
            LastManualResponse = $"从站异常 0x{(byte)response.ExceptionCode!.Value:X2}（{response.ExceptionCode}），耗时 {elapsed.TotalMilliseconds:F1} ms";
            return;
        }

        if (request.Function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs)
        {
            bool[] values = response.GetBits();
            for (int index = 0; index < values.Length; index++)
            {
                ManualResults.Add(new RegisterGridItem
                {
                    Address = request.Address + index,
                    Value = values[index] ? "1 (ON)" : "0 (OFF)",
                    HexValue = values[index] ? "0x01" : "0x00",
                    BinaryValue = RegisterGridItem.FormatBinary(values[index]),
                    Status = "正常",
                    State = GridValueState.Fresh
                });
            }
        }
        else if (request.Function is ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters)
        {
            ushort[] values = response.GetRegisters();
            for (int index = 0; index < values.Length; index++)
            {
                ManualResults.Add(new RegisterGridItem
                {
                    Address = request.Address + index,
                    Value = values[index].ToString(CultureInfo.InvariantCulture),
                    HexValue = $"0x{values[index]:X4}",
                    BinaryValue = RegisterGridItem.FormatBinary(values[index]),
                    TextValue = RegisterToAscii(values[index]),
                    Status = "正常",
                    State = GridValueState.Fresh
                });
            }
        }

        LastManualResponse = $"命令执行成功，返回 {ManualResults.Count} 项，耗时 {elapsed.TotalMilliseconds:F1} ms";
    }

    private ToolLogContext SessionContext() => new() { SessionId = SessionId };

    private ToolLogContext RequestContext(byte unitId, ModbusRequest request, TimeSpan? elapsed = null) => new()
    {
        SessionId = SessionId,
        UnitId = unitId,
        FunctionCode = (byte)request.Function,
        RegisterAddress = request.Address,
        ElapsedMilliseconds = elapsed?.TotalMilliseconds
    };

    private void OnFrameSent(object? sender, TransportDataEventArgs args)
    {
        FrameSent?.Invoke(args);
        App.Log.Write(ToolLogLevel.Trace, "主站.报文", $"发送 {args.Data.Length} 字节", (string?)null,
            context: ToolLogContext.ForFrame(SessionId, args.Data.Span, Connection.Kind == MasterConnectionKind.Tcp));
    }

    private void OnFrameReceived(object? sender, TransportDataEventArgs args)
    {
        FrameReceived?.Invoke(args);
        App.Log.Write(ToolLogLevel.Trace, "主站.报文", $"接收 {args.Data.Length} 字节", (string?)null,
            context: ToolLogContext.ForFrame(SessionId, args.Data.Span, Connection.Kind == MasterConnectionKind.Tcp));
    }

    public event Action<TransportDataEventArgs>? FrameSent;
    public event Action<TransportDataEventArgs>? FrameReceived;

    private void OnResponseReceived(object? sender, MasterResponseEventArgs args)
    {
        _responses.Enqueue(args);
        App.Log.Write(args.Response.IsException ? ToolLogLevel.Warning : ToolLogLevel.Trace,
            "主站.通信",
            $"{(args.IsPoll ? "轮询" : "手动")}命令完成，站号 {args.UnitId}，功能码 0x{(byte)args.Request.Function:X2}"
                + (args.Response.IsException ? $"，从站异常 {args.Response.ExceptionCode}" : string.Empty),
            (string?)null,
            context: RequestContext(args.UnitId, args.Request, args.ResponseTime));
    }

    private void OnCommandFailed(object? sender, MasterErrorEventArgs args)
    {
        _errors.Enqueue(args);
        App.Log.Write(args.WillRetry ? ToolLogLevel.Warning : ToolLogLevel.Error, "主站.通信",
            $"{(args.IsPoll ? "轮询" : "手动")}命令失败，站号 {args.UnitId}，{(args.WillRetry ? "将重试" : "不再重试")}",
            args.Exception,
            context: RequestContext(args.UnitId, args.Request));
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        int processed = 0;
        while (processed++ < 1000 && _responses.TryDequeue(out MasterResponseEventArgs? response))
        {
            ProcessResponse(response);
        }

        if (_errors.TryDequeue(out MasterErrorEventArgs? firstError))
        {
            MasterErrorEventArgs error = firstError;
            ApplyErrorState(error);
            while (_errors.TryDequeue(out MasterErrorEventArgs? latest))
            {
                error = latest;
                ApplyErrorState(latest);
            }

            StatusMessage = $"{(error.IsPoll ? "轮询" : "手动")}错误（站号 {error.UnitId}）：{error.Exception.Message}{(error.WillRetry ? "，将重试" : string.Empty)}";
        }

        if (_service is not null)
        {
            MasterStatisticsSnapshot statistics = _service.GetStatistics();
            StatisticsText = $"发送 {statistics.SentFrames} | 接收 {statistics.ReceivedFrames} | 成功 {statistics.SuccessfulExchanges} | " +
                $"超时 {statistics.Timeouts} | 错误 {statistics.Errors} | 重试 {statistics.Retries} | 重连 {statistics.Reconnects} | " +
                $"丢弃轮询 {statistics.DroppedPollCommands} | 队列 {_service.QueuedCommandCount} | " +
                $"响应 {statistics.LastResponseMilliseconds:F3} ms（平均 {statistics.AverageResponseMilliseconds:F3} ms）";
        }
    }

    private void ProcessResponse(MasterResponseEventArgs args)
    {
        ProcessWriteResponse(args);
        if (args.Schedule is not null && _healthLookup.TryGetValue(args.Schedule.DeviceId, out DeviceHealthItem? health))
        {
            if (args.Response.IsException)
            {
                health.RecordError();
            }
            else
            {
                health.RecordSuccess(args.ResponseTime);
            }
        }

        if (args.Schedule is not null && _blockLookup.TryGetValue(args.Schedule.BlockId, out MasterBlockItem? block))
        {
            block.SetResponse(args.ResponseTime, args.Response.IsException);
            if (ReferenceEquals(block, SelectedBlock))
            {
                if (args.Response.IsException)
                {
                    MarkRegisterRows(GridValueState.Error, "从站异常", args.UnitId);
                }
                else
                {
                    UpdateRegisterRows(args);
                }
            }
        }

        foreach (MasterPointValue value in args.PointValues)
        {
            if (_pointLookup.TryGetValue(value.PointId, out MasterPointItem? point))
            {
                point.SetValue(value);
            }
        }
    }

    private void ProcessWriteResponse(MasterResponseEventArgs args)
    {
        if (args.IsPoll
            || args.Request.Function is not (ModbusFunction.WriteSingleCoil or ModbusFunction.WriteSingleRegister)
            || !_writePending.Complete(args.UnitId, args.Request.Address))
        {
            return;
        }

        bool hasRow = _registerLookup.TryGetValue(args.Request.Address, out RegisterGridItem? row);
        if (args.Response.IsException)
        {
            if (hasRow)
            {
                row!.Status = "写入失败";
                row.State = GridValueState.Error;
            }

            StatusMessage = $"写入失败：地址 {args.Request.Address}，从站返回 {args.Response.ExceptionCode}";
            App.Log.Write(ToolLogLevel.Error, "主站.写入", StatusMessage, (string?)null,
                context: RequestContext(args.UnitId, args.Request, args.ResponseTime));
            return;
        }

        if (hasRow)
        {
            SetRegisterValue(row!, args.Request.Function, args.Response.Value);
            row!.Status = "已写入";
            row.State = GridValueState.Fresh;
            StatusMessage = $"已写入地址 {row.Address} = {row.Value}（{args.ResponseTime.TotalMilliseconds:F1} ms）";
        }
        else
        {
            StatusMessage = $"已写入地址 {args.Request.Address}（{args.ResponseTime.TotalMilliseconds:F1} ms）";
        }

        App.Log.Write(ToolLogLevel.Information, "主站.写入", StatusMessage, (string?)null,
            context: RequestContext(args.UnitId, args.Request, args.ResponseTime));
    }

    private void UpdateRegisterRows(MasterResponseEventArgs args)
    {
        int address = args.Request.Address;
        if (args.Request.Function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs)
        {
            bool[] values = args.Response.GetBits();
            for (int index = 0; index < values.Length; index++)
            {
                if (_registerLookup.TryGetValue(address + index, out RegisterGridItem? row))
                {
                    if (_writePending.IsPending(args.UnitId, (ushort)(address + index)))
                    {
                        continue;
                    }

                    row.Value = values[index] ? "1 (ON)" : "0 (OFF)";
                    row.HexValue = values[index] ? "0x01" : "0x00";
                    row.BinaryValue = RegisterGridItem.FormatBinary(values[index]);
                    row.Status = $"{args.ResponseTime.TotalMilliseconds:F1} ms";
                    row.State = GridValueState.Fresh;
                }
            }
        }
        else if (args.Request.Function is ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters)
        {
            ushort[] values = args.Response.GetRegisters();
            for (int index = 0; index < values.Length; index++)
            {
                if (_registerLookup.TryGetValue(address + index, out RegisterGridItem? row))
                {
                    if (_writePending.IsPending(args.UnitId, (ushort)(address + index)))
                    {
                        continue;
                    }

                    row.Value = values[index].ToString(CultureInfo.InvariantCulture);
                    row.HexValue = $"0x{values[index]:X4}";
                    row.BinaryValue = RegisterGridItem.FormatBinary(values[index]);
                    row.TextValue = RegisterToAscii(values[index]);
                    row.Status = $"{args.ResponseTime.TotalMilliseconds:F1} ms";
                    row.State = GridValueState.Fresh;
                }
            }
        }
    }

    private void ApplyErrorState(MasterErrorEventArgs args)
    {
        if (args.Schedule is null
            || !_blockLookup.TryGetValue(args.Schedule.BlockId, out MasterBlockItem? block))
        {
            return;
        }

        bool isTimeout = args.Exception is TimeoutException;
        block.SetError(isTimeout ? "响应超时" : "通信错误");
        if (_healthLookup.TryGetValue(args.Schedule.DeviceId, out DeviceHealthItem? health))
        {
            if (isTimeout)
            {
                health.RecordTimeout();
            }
            else
            {
                health.RecordError();
            }
        }

        if (ReferenceEquals(block, SelectedBlock))
        {
            MarkRegisterRows(
                isTimeout ? GridValueState.Stale : GridValueState.Error,
                isTimeout ? "超时" : "错误",
                args.UnitId);
        }
    }

    private void MarkRegisterRows(GridValueState state, string status, byte unitId)
    {
        foreach (RegisterGridItem row in Registers)
        {
            if (_writePending.IsPending(unitId, (ushort)row.Address))
            {
                continue;
            }

            row.State = state;
            row.Status = status;
        }
    }

    private void ResetTrendService()
    {
        _trendService.SampleRecorded -= OnTrendSampleRecorded;
        _trendService = new TrendService();
        _trendService.SampleRecorded += OnTrendSampleRecorded;
        TrendSeriesChanged?.Invoke();
    }

    private bool _trendPushFailureLogged;

    private void OnTrendSampleRecorded(object? sender, TrendSampleEventArgs args)
    {
        // 该回调运行在通信线程，视图订阅方异常不得反向影响轮询与历史记录。
        try
        {
            TrendSampleAvailable?.Invoke(args.PointId, args.Sample);
            _trendPushFailureLogged = false;
        }
        catch (Exception exception)
        {
            if (!_trendPushFailureLogged)
            {
                _trendPushFailureLogged = true;
                App.Log.Write(
                    ToolLogLevel.Warning,
                    "趋势",
                    $"趋势采样推送失败（点位 {args.PointId}）：{exception.Message}");
            }
        }
    }

    private async Task ImportPointsAsync()
    {
        if (SelectedBlock is null || !IsSelectedBlockRegister)
        {
            return;
        }

        OpenFileDialog dialog = new() { Filter = "CSV 点位表 (*.csv)|*.csv|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IReadOnlyList<CsvDataRow> rows = await Task.Run(() => CsvService.Read(dialog.FileName)).ConfigureAwait(true);
        MasterReadBlock block = SelectedBlock.Model;
        List<MasterDataPoint> parsed = [];
        List<string> problems = [];
        foreach (CsvDataRow row in rows)
        {
            try
            {
                MasterDataPoint point = new()
                {
                    Name = FindField(row, "Name", "名称") ?? throw new InvalidDataException("缺少 Name（名称）列。"),
                    AddressOffset = ParseUInt16(FindField(row, "AddressOffset", "地址偏移", "偏移")
                        ?? throw new InvalidDataException("缺少 AddressOffset（地址偏移）列。")),
                    DataType = ParseDataType(FindField(row, "DataType", "数据类型")),
                    ByteOrder = ParseByteOrder(FindField(row, "ByteOrder", "字节序")),
                    Scale = ParseOptionalDouble(FindField(row, "Scale", "倍率"), 1),
                    Offset = ParseOptionalDouble(FindField(row, "Offset", "偏移量"), 0),
                    Unit = FindField(row, "Unit", "单位") ?? string.Empty,
                    HistoryEnabled = ParseOptionalBool(FindField(row, "HistoryEnabled", "历史")),
                    GroupName = NormalizeGroupName(FindField(row, "GroupName", "分组")),
                    Note = FindField(row, "Note", "备注", "说明") ?? string.Empty
                };
                if (point.AddressOffset + RegisterConverter.GetRegisterCount(point.DataType) > block.Quantity)
                {
                    throw new InvalidDataException("点位超出当前读取块范围。");
                }

                parsed.Add(point);
            }
            catch (Exception exception)
            {
                problems.Add($"第 {row.LineNumber} 行：{exception.Message}");
            }
        }

        List<MasterDataPoint> conflicting = [];
        List<MasterDataPoint> accepted = [];
        foreach (MasterDataPoint point in parsed)
        {
            bool conflicts = block.Points.Any(existing => Overlaps(existing, point))
                || accepted.Any(existing => Overlaps(existing, point));
            if (conflicts)
            {
                conflicting.Add(point);
                problems.Add($"点位“{point.Name}”（偏移 {point.AddressOffset}）与已有点位地址重叠。");
            }

            accepted.Add(point);
        }

        bool overwrite = false;
        if (problems.Count > 0)
        {
            string detail = string.Join("\n", problems.Take(6));
            if (problems.Count > 6)
            {
                detail += $"\n……共 {problems.Count} 项";
            }

            MessageBoxResult choice = MessageBox.Show(
                $"导入校验发现 {problems.Count} 个问题：\n{detail}\n\n" +
                "是：跳过无效/冲突点位，仅导入有效项\n" +
                "否：覆盖与导入范围重叠的已有点位\n" +
                "取消：终止导入",
                "点位导入校验",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel)
            {
                StatusMessage = "已取消点位导入";
                return;
            }

            overwrite = choice == MessageBoxResult.No;
        }

        List<MasterDataPoint> imported;
        if (overwrite)
        {
            foreach (MasterDataPoint existing in block.Points
                .Where(existing => parsed.Any(point => Overlaps(existing, point)))
                .ToArray())
            {
                block.Points.Remove(existing);
                _pointLookup.Remove(existing.Id);
            }

            imported = [];
            foreach (MasterDataPoint point in parsed)
            {
                imported.RemoveAll(existing => Overlaps(existing, point));
                imported.Add(point);
            }
        }
        else
        {
            HashSet<MasterDataPoint> skipped = [.. conflicting];
            imported = parsed.Where(point => !skipped.Contains(point)).ToList();
        }

        foreach (MasterDataPoint point in imported)
        {
            block.Points.Add(point);
            _pointLookup[point.Id] = new MasterPointItem(point);
        }

        RebuildPoints();
        MarkDirty();
        TrendSeriesChanged?.Invoke();
        StatusMessage = $"已导入 {imported.Count} 个点位"
            + (problems.Count > 0 ? $"，校验发现 {problems.Count} 项问题（{(overwrite ? "已覆盖重叠点位" : "已跳过无效项")}）" : string.Empty);
    }

    private static bool Overlaps(MasterDataPoint left, MasterDataPoint right)
    {
        int leftStart = left.AddressOffset;
        int leftEnd = leftStart + RegisterConverter.GetRegisterCount(left.DataType);
        int rightStart = right.AddressOffset;
        int rightEnd = rightStart + RegisterConverter.GetRegisterCount(right.DataType);
        return leftStart < rightEnd && rightStart < leftEnd;
    }

    private static string? FindField(CsvDataRow row, params string[] names)
    {
        foreach (string name in names)
        {
            if (row.Fields.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static ModbusDataType ParseDataType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ModbusDataType.UInt16;
        }

        if (Enum.TryParse(text, true, out ModbusDataType dataType))
        {
            return dataType;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
        {
            throw new InvalidDataException($"无法识别的数据类型“{text}”。");
        }

        return code switch
        {
            0 or 2 => ModbusDataType.UInt16,
            1 => ModbusDataType.Int16,
            3 or 5 => ModbusDataType.UInt32,
            4 => ModbusDataType.Int32,
            6 => ModbusDataType.Float,
            7 => ModbusDataType.Double,
            _ => throw new InvalidDataException($"数据类型编号 {code} 无效。")
        };
    }

    private static ModbusByteOrder ParseByteOrder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ModbusByteOrder.Order1032;
        }

        if (Enum.TryParse(text, true, out ModbusByteOrder byteOrder))
        {
            return byteOrder;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) && code is >= 0 and <= 3)
        {
            return (ModbusByteOrder)code;
        }

        throw new InvalidDataException($"无法识别的字节序“{text}”。");
    }

    private static double ParseOptionalDouble(string? text, double fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : ParseDouble(text);

    private static bool ParseOptionalBool(string? text) =>
        !string.IsNullOrWhiteSpace(text) && bool.TryParse(text, out bool value) && value;

    private async Task ExportPointsAsync()
    {
        SaveFileDialog dialog = CreateCsvDialog("导出点位 IO 表", "MasterPoints");
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IEnumerable<IEnumerable<string?>> Rows()
        {
            foreach (MasterDevice device in Workspace.Devices)
            {
                foreach (MasterReadBlock block in device.ReadBlocks)
                {
                    foreach (MasterDataPoint point in block.Points)
                    {
                        yield return
                        [
                            device.Name, device.UnitId.ToString(CultureInfo.InvariantCulture), block.Name,
                            ((byte)block.Function).ToString("X2"), block.StartAddress.ToString(CultureInfo.InvariantCulture),
                            point.Name, point.AddressOffset.ToString(CultureInfo.InvariantCulture),
                            point.DataType.ToString(), point.ByteOrder.ToString(),
                            point.Scale.ToString("R", CultureInfo.InvariantCulture), point.Offset.ToString("R", CultureInfo.InvariantCulture),
                            point.Unit, point.HistoryEnabled.ToString()
                        ];
                    }
                }
            }
        }

        await CsvService.WriteAsync(dialog.FileName,
            ["Device", "UnitId", "Block", "Function", "BlockStart", "Name", "AddressOffset", "DataType", "ByteOrder", "Scale", "Offset", "Unit", "HistoryEnabled"],
            Rows()).ConfigureAwait(true);
        StatusMessage = $"点位 IO 表已导出：{dialog.FileName}";
    }

    private async Task ExportRegistersAsync()
    {
        if (SelectedBlock is null)
        {
            return;
        }

        SaveFileDialog dialog = CreateCsvDialog("导出当前寄存器表", "MasterRegisters");
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await CsvService.WriteAsync(dialog.FileName,
            ["Address", "AddressHex", "Value", "HexValue", "TextValue", "Status"],
            Registers.Select(row => (IEnumerable<string?>)[
                row.Address.ToString(CultureInfo.InvariantCulture), row.AddressHex, row.Value, row.HexValue, row.TextValue, row.Status
            ])).ConfigureAwait(true);
        StatusMessage = $"当前寄存器表已导出：{dialog.FileName}";
    }

    private async Task ExportHistoryAsync()
    {
        SaveFileDialog dialog = CreateCsvDialog("导出历史趋势", "MasterHistory");
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IEnumerable<IEnumerable<string?>> Rows()
        {
            foreach (MasterPointItem point in _pointLookup.Values.Where(point => point.HistoryEnabled))
            {
                foreach (TrendSample sample in _trendService.GetSnapshot(point.Id))
                {
                    yield return
                    [
                        point.Name, point.Id.ToString(), sample.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        sample.Value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                        sample.IsBreak.ToString(), point.Unit
                    ];
                }
            }
        }

        await CsvService.WriteAsync(dialog.FileName,
            ["Name", "PointId", "Timestamp", "Value", "IsBreak", "Unit"], Rows()).ConfigureAwait(true);
        StatusMessage = $"历史趋势已导出：{dialog.FileName}";
    }

    private static SaveFileDialog CreateCsvDialog(string title, string prefix) => new()
    {
        Title = title,
        Filter = "CSV 文件 (*.csv)|*.csv",
        FileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        AddExtension = true,
        DefaultExt = ".csv"
    };

    private void RefreshPorts()
    {
        PortNames.Clear();
        foreach (string port in SerialPort.GetPortNames().OrderBy(port => port, StringComparer.OrdinalIgnoreCase))
        {
            PortNames.Add(port);
        }
    }

    private void HandleCommandError(Exception exception)
    {
        StatusMessage = $"操作失败：{exception.Message}";
        App.Log.Write(ToolLogLevel.Error, "主站.操作", exception.Message, exception);
        ErrorOccurred?.Invoke(exception.Message);
    }

    private static string[] SplitValues(string text)
    {
        return text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static ushort ParseUInt16(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static bool ParseBit(string text)
    {
        text = text.Trim();
        return text.ToUpperInvariant() switch
        {
            "1" or "ON" or "TRUE" or "FF00" or "0XFF00" => true,
            "0" or "OFF" or "FALSE" or "0000" or "0X0000" => false,
            _ => throw new InvalidDataException($"无法识别线圈值“{text}”，请输入 0、1、ON 或 OFF。")
        };
    }

    private static double ParseDouble(string text)
    {
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string RegisterToAscii(ushort value)
    {
        byte[] bytes = [(byte)(value >> 8), (byte)value];
        return new string(bytes.Select(value => value is >= 32 and <= 126 ? (char)value : '.').ToArray());
    }
}

