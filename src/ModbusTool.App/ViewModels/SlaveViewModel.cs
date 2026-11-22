// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using ModbusCore.ByteConvert;
using ModbusCore.Common;
using ModbusCore.ModbusDataModel;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;
using ModbusSlave;
using ModbusTool.App.Controls;
using ModbusTool.App.Services;

namespace ModbusTool.App.ViewModels;

public sealed class SlaveViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SlaveStationManager _stations = new([]);
    private readonly SlaveService _service;
    private readonly SimulationService _simulation;
    private readonly RegPersistManager _persistence = new();
    private readonly SlaveConfigurationManager _configurationManager = new();
    private readonly string _configurationFilePath = Path.Combine(Services.AppDataPathHelper.RuntimeCacheFolder, "slave-config.json");
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private readonly Dictionary<byte, string> _dataPaths = [];
    private readonly DispatcherTimer _uiTimer;
    private SlaveStationItem? _selectedStation;
    private ModbusDataArea _selectedArea = ModbusDataArea.HoldingRegisters;
    private int _gridStartAddress;
    private int _gridDisplayCount = 100;
    private string _selectedEncoding = "GB2312";
    private bool _swapStringBytes;
    private int _stringAddress;
    private int _stringRegisterCount = 8;
    private string _stringText = string.Empty;
    private string _decodedString = string.Empty;
    private string _dataFilePath = string.Empty;
    private byte _newUnitId = 2;
    private bool _useTcp = true;
    private bool _useSerial;
    // 默认监听本机环回（本地主从互测最常见场景）；需要局域网访问时用户可在下拉框切回 0.0.0.0 全网卡监听。
    private string _tcpAddress = "127.0.0.1";
    private int _tcpPort = 502;
    private string _portName = "COM1";
    private int _baudRate = 9600;
    private string _baudRateText = "9600";
    private string _baudRateError = string.Empty;
    private int _dataBits = 8;
    private string _parity = nameof(System.IO.Ports.Parity.None);
    private string _stopBits = nameof(System.IO.Ports.StopBits.One);
    private bool _isRunning;
    private bool _isDataRunning;
    private bool _isOutputRunning;
    private string _statusMessage = "从站离线编辑模式";
    private string _lastError = "无";
    private string _statisticsText = "暂无通信统计";
    private DataSimulationMode _simulationMode = DataSimulationMode.Increment;
    private ModbusDataArea _simulationArea = ModbusDataArea.HoldingRegisters;
    private int _simulationStartAddress;
    private int _simulationRegisterCount = 1;
    private int _simulationIntervalMs = 100;
    private ModbusDataType _simulationDataType = ModbusDataType.UInt16;
    private ModbusByteOrder _simulationByteOrder = ModbusByteOrder.Order1032;
    private double _simulationMinimum;
    private double _simulationMaximum = 100;
    private double _simulationStep = 1;
    private int _noiseMinimumIntervalMs;
    private int _noiseMaximumIntervalMs = 29;
    private int _noiseMinimumLength;
    private int _noiseMaximumLength = 9;
    private bool _sendSimulationToTcp = true;
    private bool _sendSimulationToSerial = true;
    private bool _faultEnabled;
    private double _dropProbabilityPercent;
    private double _invalidValueProbabilityPercent;
    private double _invalidCrcProbabilityPercent;
    private int _responseDelayMs;
    private bool _forceException;
    private ModbusExceptionCode _forcedExceptionCode = ModbusExceptionCode.SlaveDeviceFailure;
    private long _totalRequests;
    private long _totalFaults;
    private long _totalDrops;
    private long _totalErrors;
    private string _listenerSummary = "TCP 127.0.0.1:502";
    private string _requestSummary = "请求 0 / 错误 0";
    private volatile bool _gridDirty;
    private bool _initialized;
    private bool _configurationLoaded;
    private bool _disposed;

    static SlaveViewModel()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public SlaveViewModel()
    {
        _service = new SlaveService(_stations);
        _simulation = new SimulationService(_stations, _service);
        _service.FrameSent += OnFrameSent;
        _service.FrameReceived += OnFrameReceived;
        _service.Error += OnServiceError;
        _service.RequestProcessed += OnRequestProcessed;
        _service.ClientConnected += OnClientConnected;
        _service.ClientDisconnected += OnClientDisconnected;
        _simulation.Error += OnSimulationError;

        AddStationCommand = new RelayCommand(_ => RunGuarded(AddStation), _ => IsOffline);
        RemoveStationCommand = new RelayCommand(_ => RunGuarded(RemoveSelectedStation), _ => IsOffline && Stations.Count > 1 && SelectedStation is not null);
        RefreshPortsCommand = new RelayCommand(_ => RefreshPorts());
        ImportLegacyConfigCommand = new AsyncRelayCommand(_ => ImportLegacyConfigAsync(), _ => IsOffline, HandleCommandError);
        SaveConfigurationCommand = new AsyncRelayCommand(_ => SaveConfigurationAsync(), onError: HandleCommandError);
        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => IsOffline, HandleCommandError);
        StopCommand = new AsyncRelayCommand(_ => StopAsync(), _ => IsRunning, HandleCommandError);
        RefreshGridCommand = new RelayCommand(_ => RunGuarded(RebuildGrid));
        ExportRegistersCommand = new AsyncRelayCommand(_ => ExportRegistersAsync(), _ => SelectedStation is not null, HandleCommandError);
        LoadDataCommand = new AsyncRelayCommand(_ => LoadDataAsync(), _ => SelectedStation is not null, HandleCommandError);
        SaveDataCommand = new AsyncRelayCommand(_ => SaveDataAsync(), _ => SelectedStation is not null, HandleCommandError);
        DecodeStringCommand = new RelayCommand(_ => RunGuarded(DecodeString), _ => IsRegisterArea && SelectedStation is not null);
        EncodeStringCommand = new RelayCommand(_ => RunGuarded(EncodeString), _ => IsRegisterArea && SelectedStation is not null);
        StartDataSimulationCommand = new AsyncRelayCommand(_ => StartDataSimulationAsync(), _ => !IsDataRunning && SelectedStation is not null, HandleCommandError);
        StopDataSimulationCommand = new AsyncRelayCommand(_ => StopDataSimulationAsync(), _ => IsDataRunning, HandleCommandError);
        StartNoiseCommand = new AsyncRelayCommand(_ => StartNoiseAsync(), _ => IsRunning && !IsOutputRunning, HandleCommandError);
        StopOutputCommand = new AsyncRelayCommand(_ => StopOutputAsync(), _ => IsOutputRunning, HandleCommandError);
        ApplyFaultsCommand = new RelayCommand(_ => RunGuarded(() => ApplyFaultSettings(true)));

        AddStationCore(1);
        SelectedStation = Stations[0];
        RefreshPorts();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTimerTick;
        _uiTimer.Start();
    }

    public event Action<TransportDataEventArgs>? FrameSent;
    public event Action<TransportDataEventArgs>? FrameReceived;
    public event Action<string>? ErrorOccurred;

    public string SessionId { get; } = $"S-{Guid.NewGuid().ToString("N")[..8]}";
    public ObservableCollection<SlaveStationItem> Stations { get; } = [];
    public ObservableCollection<SlaveClientItem> Clients { get; } = [];
    public ObservableCollection<RegisterGridItem> Registers { get; } = [];
    public ObservableCollection<string> PortNames { get; } = [];

    public IReadOnlyList<SlaveAreaOption> DataAreas { get; } =
    [
        new(ModbusDataArea.Coils, "01 线圈"),
        new(ModbusDataArea.DiscreteInputs, "02 离散输入"),
        new(ModbusDataArea.HoldingRegisters, "03 保持寄存器"),
        new(ModbusDataArea.InputRegisters, "04 输入寄存器")
    ];

    public IReadOnlyList<SlaveAreaOption> RegisterAreas { get; } =
    [
        new(ModbusDataArea.HoldingRegisters, "03 保持寄存器"),
        new(ModbusDataArea.InputRegisters, "04 输入寄存器")
    ];

    public IReadOnlyList<string> Encodings { get; } = ["GB2312", "UTF-8", "Unicode", "ASCII"];
    public IReadOnlyList<DataSimulationMode> SimulationModes { get; } = Enum.GetValues<DataSimulationMode>();
    public IReadOnlyList<ModbusDataType> DataTypes { get; } = Enum.GetValues<ModbusDataType>();
    public IReadOnlyList<ModbusByteOrder> ByteOrders { get; } = Enum.GetValues<ModbusByteOrder>();
    public IReadOnlyList<ModbusExceptionCode> ExceptionCodes { get; } = Enum.GetValues<ModbusExceptionCode>();
    public IReadOnlyList<string> Parities { get; } = Enum.GetNames<Parity>();
    public IReadOnlyList<string> StopBitNames { get; } = Enum.GetNames<StopBits>();
    public IReadOnlyList<int> BaudRates => SerialBaudRates.Presets;

    public RelayCommand AddStationCommand { get; }
    public RelayCommand RemoveStationCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public AsyncRelayCommand ImportLegacyConfigCommand { get; }
    public AsyncRelayCommand SaveConfigurationCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand RefreshGridCommand { get; }
    public AsyncRelayCommand ExportRegistersCommand { get; }
    public AsyncRelayCommand LoadDataCommand { get; }
    public AsyncRelayCommand SaveDataCommand { get; }
    public RelayCommand DecodeStringCommand { get; }
    public RelayCommand EncodeStringCommand { get; }
    public AsyncRelayCommand StartDataSimulationCommand { get; }
    public AsyncRelayCommand StopDataSimulationCommand { get; }
    public AsyncRelayCommand StartNoiseCommand { get; }
    public AsyncRelayCommand StopOutputCommand { get; }
    public RelayCommand ApplyFaultsCommand { get; }

    public SlaveStationItem? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (SetProperty(ref _selectedStation, value))
            {
                if (value is not null)
                {
                    if (!_dataPaths.TryGetValue(value.UnitId, out string? path))
                    {
                        path = GetDefaultDataPath(value.UnitId);
                        _dataPaths[value.UnitId] = path;
                    }

                    DataFilePath = path;
                }

                RebuildGrid();
            }
        }
    }

    public ModbusDataArea SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (SetProperty(ref _selectedArea, value))
            {
                OnPropertyChanged(nameof(IsRegisterArea));
                RebuildGrid();
            }
        }
    }

    public bool IsRegisterArea => SelectedArea is ModbusDataArea.HoldingRegisters or ModbusDataArea.InputRegisters;
    public int GridStartAddress { get => _gridStartAddress; set => SetProperty(ref _gridStartAddress, value); }
    public int GridDisplayCount { get => _gridDisplayCount; set => SetProperty(ref _gridDisplayCount, value); }
    public string SelectedEncoding
    {
        get => _selectedEncoding;
        set
        {
            if (SetProperty(ref _selectedEncoding, value))
            {
                RefreshGridValues();
            }
        }
    }

    public bool SwapStringBytes { get => _swapStringBytes; set => SetProperty(ref _swapStringBytes, value); }
    public int StringAddress { get => _stringAddress; set => SetProperty(ref _stringAddress, value); }
    public int StringRegisterCount { get => _stringRegisterCount; set => SetProperty(ref _stringRegisterCount, value); }
    public string StringText { get => _stringText; set => SetProperty(ref _stringText, value); }
    public string DecodedString { get => _decodedString; private set => SetProperty(ref _decodedString, value); }
    public string DataFilePath
    {
        get => _dataFilePath;
        set
        {
            if (SetProperty(ref _dataFilePath, value))
            {
                OnPropertyChanged(nameof(DataFileName));
                if (SelectedStation is not null)
                {
                    _dataPaths[SelectedStation.UnitId] = value;
                }
            }
        }
    }

    public byte NewUnitId { get => _newUnitId; set => SetProperty(ref _newUnitId, value); }
    public bool UseTcp { get => _useTcp; set => SetProperty(ref _useTcp, value); }
    public bool UseSerial { get => _useSerial; set => SetProperty(ref _useSerial, value); }
    public string TcpAddress { get => _tcpAddress; set => SetProperty(ref _tcpAddress, value); }
    public int TcpPort { get => _tcpPort; set => SetProperty(ref _tcpPort, value); }
    public string PortName { get => _portName; set => SetProperty(ref _portName, value); }
    public int BaudRate
    {
        get => _baudRate;
        set
        {
            if (SetProperty(ref _baudRate, value))
            {
                _baudRateText = value.ToString(CultureInfo.InvariantCulture);
                BaudRateError = string.Empty;
                OnPropertyChanged(nameof(BaudRateText));
            }
        }
    }

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
                _baudRate = baudRate;
                OnPropertyChanged(nameof(BaudRate));
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
    public int DataBits { get => _dataBits; set => SetProperty(ref _dataBits, value); }
    public string Parity { get => _parity; set => SetProperty(ref _parity, value); }
    public string StopBits { get => _stopBits; set => SetProperty(ref _stopBits, value); }
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(IsOffline)); } }
    public bool IsOffline => !IsRunning;
    public bool IsDataRunning { get => _isDataRunning; private set => SetProperty(ref _isDataRunning, value); }
    public bool IsOutputRunning { get => _isOutputRunning; private set => SetProperty(ref _isOutputRunning, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }
    public string StatisticsText { get => _statisticsText; private set => SetProperty(ref _statisticsText, value); }
    public string ListenerSummary { get => _listenerSummary; private set => SetProperty(ref _listenerSummary, value); }
    public string RequestSummary { get => _requestSummary; private set => SetProperty(ref _requestSummary, value); }
    public string DataFileName => Path.GetFileName(DataFilePath);

    public DataSimulationMode SimulationMode { get => _simulationMode; set => SetProperty(ref _simulationMode, value); }
    public ModbusDataArea SimulationArea { get => _simulationArea; set => SetProperty(ref _simulationArea, value); }
    public int SimulationStartAddress { get => _simulationStartAddress; set => SetProperty(ref _simulationStartAddress, value); }
    public int SimulationRegisterCount { get => _simulationRegisterCount; set => SetProperty(ref _simulationRegisterCount, value); }
    public int SimulationIntervalMs { get => _simulationIntervalMs; set => SetProperty(ref _simulationIntervalMs, value); }
    public ModbusDataType SimulationDataType { get => _simulationDataType; set => SetProperty(ref _simulationDataType, value); }
    public ModbusByteOrder SimulationByteOrder { get => _simulationByteOrder; set => SetProperty(ref _simulationByteOrder, value); }
    public double SimulationMinimum { get => _simulationMinimum; set => SetProperty(ref _simulationMinimum, value); }
    public double SimulationMaximum { get => _simulationMaximum; set => SetProperty(ref _simulationMaximum, value); }
    public double SimulationStep { get => _simulationStep; set => SetProperty(ref _simulationStep, value); }
    public int NoiseMinimumIntervalMs { get => _noiseMinimumIntervalMs; set => SetProperty(ref _noiseMinimumIntervalMs, value); }
    public int NoiseMaximumIntervalMs { get => _noiseMaximumIntervalMs; set => SetProperty(ref _noiseMaximumIntervalMs, value); }
    public int NoiseMinimumLength { get => _noiseMinimumLength; set => SetProperty(ref _noiseMinimumLength, value); }
    public int NoiseMaximumLength { get => _noiseMaximumLength; set => SetProperty(ref _noiseMaximumLength, value); }
    public bool SendSimulationToTcp { get => _sendSimulationToTcp; set => SetProperty(ref _sendSimulationToTcp, value); }
    public bool SendSimulationToSerial { get => _sendSimulationToSerial; set => SetProperty(ref _sendSimulationToSerial, value); }
    public bool FaultEnabled { get => _faultEnabled; set => SetProperty(ref _faultEnabled, value); }
    public double DropProbabilityPercent { get => _dropProbabilityPercent; set => SetProperty(ref _dropProbabilityPercent, value); }
    public double InvalidValueProbabilityPercent { get => _invalidValueProbabilityPercent; set => SetProperty(ref _invalidValueProbabilityPercent, value); }
    public double InvalidCrcProbabilityPercent { get => _invalidCrcProbabilityPercent; set => SetProperty(ref _invalidCrcProbabilityPercent, value); }
    public int ResponseDelayMs { get => _responseDelayMs; set => SetProperty(ref _responseDelayMs, value); }
    public bool ForceException { get => _forceException; set => SetProperty(ref _forceException, value); }
    public ModbusExceptionCode ForcedExceptionCode { get => _forcedExceptionCode; set => SetProperty(ref _forcedExceptionCode, value); }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (File.Exists(_configurationFilePath))
        {
            await _configurationManager.LoadAsync(_configurationFilePath).ConfigureAwait(true);
            ApplyConfiguration();
        }

        _configurationLoaded = true;
        List<string> warnings = await LoadAllStationDataAsync().ConfigureAwait(true);
        StatusMessage = warnings.Count == 0 ? "配置和寄存器持久化数据已加载" : string.Join(" ", warnings);
    }

    private async Task ImportLegacyConfigAsync()
    {
        if (!IsOffline || IsDataRunning)
        {
            throw new InvalidDataException("请先停止监听和数据仿真，再导入旧配置。");
        }

        OpenFileDialog dialog = new()
        {
            Title = "导入旧版 MBSlave config.txt",
            Filter = "旧版配置 (config.txt)|config.txt|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _configurationManager.LoadAsync(dialog.FileName).ConfigureAwait(true);
        if (!_configurationManager.LegacyImported)
        {
            throw new InvalidDataException("所选文件不是旧版 MBSlave 配置。");
        }

        Dictionary<byte, string> legacyDataSources = _configurationManager.Current.Stations
            .ToDictionary(station => station.UnitId, station => station.DataFilePath);
        foreach (SlaveStationConfiguration station in _configurationManager.Current.Stations)
        {
            station.DataFilePath = GetDefaultDataPath(station.UnitId);
        }

        ApplyConfiguration();
        _configurationLoaded = true;
        List<string> warnings = await LoadAllStationDataAsync(legacyDataSources).ConfigureAwait(true);
        StatusMessage = $"旧配置已迁移，原 config.txt 和 data.bin 不会被覆盖。{string.Join(" ", warnings)}";
        App.Log.Write(ToolLogLevel.Information, "从站.操作", StatusMessage);
    }

    private async Task SaveConfigurationAsync()
    {
        SlaveConfiguration configuration = _configurationManager.Current;
        configuration.Stations = Stations.Select(station => new SlaveStationConfiguration
        {
            UnitId = station.UnitId,
            DataFilePath = _dataPaths[station.UnitId]
        }).ToList();
        configuration.UseTcp = UseTcp;
        configuration.UseSerial = UseSerial;
        configuration.TcpAddress = TcpAddress;
        configuration.TcpPort = TcpPort;
        configuration.PortName = PortName;
        configuration.BaudRate = BaudRate;
        configuration.DataBits = DataBits;
        configuration.Parity = Parity;
        configuration.StopBits = StopBits;
        configuration.SelectedEncoding = SelectedEncoding;
        configuration.SwapStringBytes = SwapStringBytes;
        configuration.GridStartAddress = GridStartAddress;
        configuration.GridDisplayCount = GridDisplayCount;
        configuration.TableDataType = SimulationDataType;
        configuration.TableByteOrder = SimulationByteOrder;
        await _configurationManager.SaveAsync(_configurationFilePath).ConfigureAwait(true);
        StatusMessage = $"从站配置已原子保存：{_configurationFilePath}";
        App.Log.Write(ToolLogLevel.Information, "从站.操作", StatusMessage);
    }

    private void ApplyConfiguration()
    {
        SlaveConfiguration configuration = _configurationManager.Current;
        foreach (byte unitId in _stations.UnitIds.ToArray())
        {
            _stations.RemoveStation(unitId);
        }

        Stations.Clear();
        _dataPaths.Clear();
        foreach (SlaveStationConfiguration station in configuration.Stations)
        {
            AddStationCore(station.UnitId);
            _dataPaths[station.UnitId] = station.DataFilePath;
        }

        SelectedStation = Stations[0];
        UseTcp = configuration.UseTcp;
        UseSerial = configuration.UseSerial;
        TcpAddress = configuration.TcpAddress;
        TcpPort = configuration.TcpPort;
        PortName = configuration.PortName;
        BaudRate = configuration.BaudRate;
        DataBits = configuration.DataBits;
        Parity = configuration.Parity;
        StopBits = configuration.StopBits;
        SelectedEncoding = configuration.SelectedEncoding;
        SwapStringBytes = configuration.SwapStringBytes;
        GridStartAddress = configuration.GridStartAddress;
        GridDisplayCount = configuration.GridDisplayCount;
        SimulationDataType = configuration.TableDataType;
        SimulationByteOrder = configuration.TableByteOrder;
        RebuildGrid();
    }

    private async Task<List<string>> LoadAllStationDataAsync(
        IReadOnlyDictionary<byte, string>? sourcePaths = null)
    {
        List<string> warnings = [];
        foreach (SlaveStationItem station in Stations)
        {
            string path = sourcePaths is not null && sourcePaths.TryGetValue(station.UnitId, out string? sourcePath)
                ? sourcePath
                : _dataPaths[station.UnitId];
            RegisterPersistenceInfo info = await _persistence.LoadAsync(path, station.Store).ConfigureAwait(true);
            if (info.Warning is not null)
            {
                warnings.Add($"站号 {station.UnitId}：{info.Warning}");
            }
            else if (info.LegacyImported)
            {
                warnings.Add($"站号 {station.UnitId}：已导入旧版 data.bin。");
            }
        }

        _gridDirty = true;
        return warnings;
    }

    public void EditValue(RegisterValueEditingEventArgs args)
    {
        try
        {
            if (SelectedStation is null)
            {
                args.Cancel = true;
                return;
            }

            ushort address = checked((ushort)args.Item.Address);
            if (SelectedArea is ModbusDataArea.Coils or ModbusDataArea.DiscreteInputs)
            {
                bool value = ParseBit(args.ProposedValue);
                if (SelectedArea == ModbusDataArea.Coils)
                {
                    SelectedStation.Store.WriteSingleCoil(address, value);
                }
                else
                {
                    SelectedStation.Store.WriteDiscreteInputs(address, [value]);
                }
            }
            else
            {
                ushort value = ParseUInt16(args.ProposedValue);
                if (SelectedArea == ModbusDataArea.HoldingRegisters)
                {
                    SelectedStation.Store.WriteSingleRegister(address, value);
                }
                else
                {
                    SelectedStation.Store.WriteInputRegisters(address, [value]);
                }
            }

            args.Item.Status = "本地编辑";
            _gridDirty = true;
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            HandleCommandError(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _uiTimer.Stop();
        await _simulation.DisposeAsync().ConfigureAwait(false);
        await _service.StopAsync().ConfigureAwait(false);
        if (_initialized)
        {
            await SaveAllAsync().ConfigureAwait(false);
        }

        if (_configurationLoaded)
        {
            await SaveConfigurationAsync().ConfigureAwait(false);
        }

        _service.FrameSent -= OnFrameSent;
        _service.FrameReceived -= OnFrameReceived;
        _service.Error -= OnServiceError;
        _service.RequestProcessed -= OnRequestProcessed;
        _service.ClientConnected -= OnClientConnected;
        _service.ClientDisconnected -= OnClientDisconnected;
        _simulation.Error -= OnSimulationError;
        await _service.DisposeAsync().ConfigureAwait(false);
    }

    private void AddStation()
    {
        if (_stations.UnitIds.Contains(NewUnitId))
        {
            throw new InvalidDataException($"站号 {NewUnitId} 已存在。");
        }

        AddStationCore(NewUnitId);
        SelectedStation = Stations[^1];
        NewUnitId = (byte)Math.Min(byte.MaxValue, NewUnitId + 1);
    }

    private void AddStationCore(byte unitId)
    {
        ModbusDataStore store = _stations.GetOrAddStation(unitId);
        SlaveStationItem station = new(unitId, store);
        store.DataChanged += (_, _) => _gridDirty = true;
        Stations.Add(station);
        _dataPaths[unitId] = GetDefaultDataPath(unitId);
    }

    private void RemoveSelectedStation()
    {
        if (SelectedStation is null || Stations.Count <= 1)
        {
            return;
        }

        byte unitId = SelectedStation.UnitId;
        int index = Stations.IndexOf(SelectedStation);
        _stations.RemoveStation(unitId);
        Stations.Remove(SelectedStation);
        _dataPaths.Remove(unitId);
        SelectedStation = Stations[Math.Min(index, Stations.Count - 1)];
        StatusMessage = $"已移除站号 {unitId}";
    }

    private async Task StartAsync()
    {
        if (!UseTcp && !UseSerial)
        {
            throw new InvalidDataException("请至少启用 Modbus TCP 或 Modbus RTU 中的一种监听方式。");
        }

        ApplyFaultSettings(false);
        try
        {
            if (UseTcp)
            {
                if (!IPAddress.TryParse(TcpAddress, out IPAddress? address))
                {
                    throw new InvalidDataException("TCP 监听地址无效。");
                }

                await _service.StartTcpAsync(address, TcpPort).ConfigureAwait(true);
            }

            if (UseSerial)
            {
                if (!SerialBaudRates.TryValidate(BaudRateText, out int baudRate, out string baudRateError))
                {
                    BaudRateError = baudRateError;
                    throw new InvalidDataException(baudRateError);
                }

                await _service.StartSerialAsync(new SerialTransportSettings
                {
                    PortName = PortName,
                    BaudRate = baudRate,
                    DataBits = DataBits,
                    Parity = Enum.Parse<Parity>(Parity),
                    StopBits = Enum.Parse<StopBits>(StopBits)
                }).ConfigureAwait(true);
            }

            IsRunning = true;
            StatusMessage = $"从站已启动：{GetConnectionSummary()}";
            App.Log.Write(ToolLogLevel.Information, "从站.通信", StatusMessage, (string?)null, context: SessionContext());
        }
        catch
        {
            await _service.StopAsync().ConfigureAwait(true);
            IsRunning = false;
            throw;
        }
    }

    private async Task StopAsync()
    {
        await _simulation.StopOutputAsync().ConfigureAwait(true);
        await _service.StopAsync().ConfigureAwait(true);
        IsRunning = false;
        Clients.Clear();
        StatusMessage = "从站监听已停止，数据仿真可继续离线运行";
        App.Log.Write(ToolLogLevel.Information, "从站.通信", StatusMessage, (string?)null, context: SessionContext());
    }

    private async Task StartDataSimulationAsync()
    {
        if (SelectedStation is null)
        {
            return;
        }

        DataSimulationSettings settings = new()
        {
            UnitId = SelectedStation.UnitId,
            Area = SimulationArea,
            StartAddress = checked((ushort)SimulationStartAddress),
            RegisterCount = SimulationRegisterCount,
            IntervalMilliseconds = SimulationIntervalMs,
            Mode = SimulationMode,
            DataType = SimulationDataType,
            ByteOrder = SimulationByteOrder,
            Minimum = SimulationMinimum,
            Maximum = SimulationMaximum,
            Step = SimulationStep
        };
        await _simulation.StartDataAsync(settings).ConfigureAwait(true);
        IsDataRunning = true;
        StatusMessage = $"站号 {SelectedStation.UnitId} 的{SimulationMode}数据仿真已启动";
    }

    private async Task StopDataSimulationAsync()
    {
        await _simulation.StopDataAsync().ConfigureAwait(true);
        IsDataRunning = false;
        StatusMessage = "数据仿真已停止";
    }

    private async Task StartNoiseAsync()
    {
        await _simulation.StartNoiseAsync(new NoiseSimulationSettings
        {
            MinimumIntervalMilliseconds = NoiseMinimumIntervalMs,
            MaximumIntervalMilliseconds = NoiseMaximumIntervalMs,
            MinimumLength = NoiseMinimumLength,
            MaximumLength = NoiseMaximumLength,
            SendToTcp = SendSimulationToTcp,
            SendToSerial = SendSimulationToSerial
        }).ConfigureAwait(true);
        IsOutputRunning = true;
        StatusMessage = "随机干扰报文仿真已启动";
    }

    private async Task StopOutputAsync()
    {
        await _simulation.StopOutputAsync().ConfigureAwait(true);
        IsOutputRunning = false;
        StatusMessage = "干扰报文仿真已停止";
    }

    private void ApplyFaultSettings(bool updateStatus)
    {
        _service.FaultInjector.Settings = new FaultInjectionSettings
        {
            Enabled = FaultEnabled,
            DropProbability = DropProbabilityPercent / 100,
            InvalidValueProbability = InvalidValueProbabilityPercent / 100,
            InvalidCrcProbability = InvalidCrcProbabilityPercent / 100,
            DelayMilliseconds = ResponseDelayMs,
            ForcedExceptionCode = ForceException ? ForcedExceptionCode : null
        };
        if (updateStatus)
        {
            StatusMessage = FaultEnabled ? "故障注入配置已应用到真实响应链路" : "故障注入已关闭";
        }
    }

    private void RebuildGrid()
    {
        if (SelectedStation is null)
        {
            Registers.Clear();
            return;
        }

        ValidateRange(GridStartAddress, GridDisplayCount);
        Registers.Clear();
        for (int offset = 0; offset < GridDisplayCount; offset++)
        {
            Registers.Add(new RegisterGridItem { Address = GridStartAddress + offset });
        }

        RefreshGridValues();
    }

    private void RefreshGridValues()
    {
        _gridDirty = false;
        if (SelectedStation is null || Registers.Count == 0)
        {
            return;
        }

        ushort startAddress = checked((ushort)Registers[0].Address);
        int count = Registers.Count;
        Encoding encoding = GetSelectedEncoding();
        if (SelectedArea is ModbusDataArea.Coils or ModbusDataArea.DiscreteInputs)
        {
            bool[] values = SelectedArea == ModbusDataArea.Coils
                ? SelectedStation.Store.ReadCoils(startAddress, count)
                : SelectedStation.Store.ReadDiscreteInputs(startAddress, count);
            for (int index = 0; index < count; index++)
            {
                Registers[index].Value = values[index] ? "1" : "0";
                Registers[index].HexValue = values[index] ? "01" : "00";
                Registers[index].BinaryValue = RegisterGridItem.FormatBinary(values[index]);
                Registers[index].TextValue = values[index] ? "ON" : "OFF";
            }

            return;
        }

        ushort[] registers = SelectedArea == ModbusDataArea.HoldingRegisters
            ? SelectedStation.Store.ReadHoldingRegisters(startAddress, count)
            : SelectedStation.Store.ReadInputRegisters(startAddress, count);
        for (int index = 0; index < count; index++)
        {
            ushort value = registers[index];
            Registers[index].Value = value.ToString(CultureInfo.InvariantCulture);
            Registers[index].HexValue = $"0x{value:X4}";
            Registers[index].BinaryValue = RegisterGridItem.FormatBinary(value);
            string text = RegisterConverter.DecodeString([value], encoding, SwapStringBytes).TrimEnd('\0');
            Registers[index].TextValue = MakeControlCharactersVisible(text);
        }
    }

    private void DecodeString()
    {
        ushort[] registers = ReadStringRegisters();
        DecodedString = RegisterConverter.DecodeString(registers, GetSelectedEncoding(), SwapStringBytes).TrimEnd('\0');
        StatusMessage = $"已按 {SelectedEncoding} 解码 {registers.Length} 个寄存器";
    }

    private void EncodeString()
    {
        ushort[] values = RegisterConverter.EncodeString(
            StringText, GetSelectedEncoding(), StringRegisterCount, SwapStringBytes);
        ValidateRange(StringAddress, StringRegisterCount);
        if (SelectedArea == ModbusDataArea.HoldingRegisters)
        {
            SelectedStation!.Store.WriteHoldingRegisters(checked((ushort)StringAddress), values);
        }
        else if (SelectedArea == ModbusDataArea.InputRegisters)
        {
            SelectedStation!.Store.WriteInputRegisters(checked((ushort)StringAddress), values);
        }
        else
        {
            throw new InvalidDataException("字符串只能写入保持寄存器或输入寄存器。");
        }

        _gridDirty = true;
        DecodedString = StringText;
        StatusMessage = $"字符串已按 {SelectedEncoding} 写入站号 {SelectedStation.UnitId}";
    }

    private ushort[] ReadStringRegisters()
    {
        ValidateRange(StringAddress, StringRegisterCount);
        return SelectedArea switch
        {
            ModbusDataArea.HoldingRegisters => SelectedStation!.Store.ReadHoldingRegisters(
                checked((ushort)StringAddress), StringRegisterCount),
            ModbusDataArea.InputRegisters => SelectedStation!.Store.ReadInputRegisters(
                checked((ushort)StringAddress), StringRegisterCount),
            _ => throw new InvalidDataException("请选择保持寄存器或输入寄存器后再解析字符串。")
        };
    }

    private async Task LoadDataAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "加载寄存器持久化文件",
            Filter = "Modbus 寄存器文件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            FileName = DataFilePath
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        DataFilePath = dialog.FileName;
        RegisterPersistenceInfo info = await _persistence.LoadAsync(DataFilePath, SelectedStation!.Store).ConfigureAwait(true);
        _gridDirty = true;
        StatusMessage = info.Warning ?? (info.LegacyImported ? "旧版 data.bin 已导入，保存时将升级为 MBD2 格式。" : "寄存器文件已加载。");
    }

    private async Task SaveDataAsync()
    {
        await _persistence.SaveAsync(DataFilePath, SelectedStation!.Store).ConfigureAwait(true);
        StatusMessage = $"站号 {SelectedStation.UnitId} 数据已保存：{DataFilePath}";
    }

    private async Task SaveAllAsync()
    {
        foreach (SlaveStationItem station in Stations)
        {
            if (_dataPaths.TryGetValue(station.UnitId, out string? path) && !string.IsNullOrWhiteSpace(path))
            {
                await _persistence.SaveAsync(path, station.Store).ConfigureAwait(false);
            }
        }
    }

    private async Task ExportRegistersAsync()
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出从站寄存器表",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"Slave_{SelectedStation!.UnitId}_{SelectedArea}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        RegisterGridItem[] snapshot = Registers.ToArray();
        await CsvService.WriteAsync(
            dialog.FileName,
            ["站号", "数据区", "地址", "值", "十六进制", "字符串", "状态"],
            snapshot.Select(row => (IEnumerable<string?>)[
                SelectedStation.UnitId.ToString(CultureInfo.InvariantCulture),
                SelectedArea.ToString(),
                row.Address.ToString(CultureInfo.InvariantCulture),
                row.Value,
                row.HexValue,
                row.TextValue,
                row.Status
            ])).ConfigureAwait(true);
        StatusMessage = $"寄存器表已导出：{dialog.FileName}";
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        for (int index = 0; index < 1000 && _uiActions.TryDequeue(out Action? action); index++)
        {
            action();
        }

        if (_gridDirty)
        {
            RefreshGridValues();
        }

        IsRunning = _service.IsTcpListening || _service.IsSerialOpen;
        IsDataRunning = _simulation.IsDataRunning;
        IsOutputRunning = _simulation.IsOutputRunning;
        StatisticsText = $"站号：{Stations.Count}    TCP 客户端：{Clients.Count}\n"
            + $"请求：{_totalRequests}    故障注入：{_totalFaults}    丢包：{_totalDrops}\n"
            + $"监听：{GetConnectionSummary()}\n数据仿真：{(IsDataRunning ? "运行中" : "停止")}    原始报文仿真：{(IsOutputRunning ? "运行中" : "停止")}";
        ListenerSummary = GetListenerSummary();
        RequestSummary = $"请求 {_totalRequests} / 错误 {_totalErrors}";
    }

    private ToolLogContext SessionContext() => new() { SessionId = SessionId };

    private ToolLogContext RequestContext(SlaveRequestEventArgs args) => new()
    {
        SessionId = SessionId,
        UnitId = args.Result.UnitId,
        FunctionCode = args.Result.Request is { } request ? (byte)request.Function : null,
        RegisterAddress = args.Result.Request?.Address,
        ElapsedMilliseconds = args.ProcessingTime.TotalMilliseconds
    };

    private void OnRequestProcessed(object? sender, SlaveRequestEventArgs args)
    {
        SlaveRequestResult result = args.Result;
        ToolLogLevel level = result.Error is not null
            ? ToolLogLevel.Error
            : result.IsDropped || result.IsFaultInjected || result.Response?.IsException == true
                ? ToolLogLevel.Warning
                : ToolLogLevel.Trace;
        string message = $"请求处理完成，站号 {result.UnitId}，耗时 {args.ProcessingTime.TotalMilliseconds:F2} ms"
            + (result.IsDropped ? "，已注入丢包" : string.Empty)
            + (result.IsFaultInjected ? $"，故障 {result.Faults}" : string.Empty)
            + (result.Response?.IsException == true ? $"，从站异常 {result.Response.ExceptionCode}" : string.Empty)
            + (result.Error is not null ? $"，{result.Error.Message}" : string.Empty);
        App.Log.Write(level, "从站.通信", message, result.Error, context: RequestContext(args));
        _uiActions.Enqueue(() =>
        {
            _totalRequests++;
            if (result.Error is not null || result.Response?.IsException == true)
            {
                _totalErrors++;
            }

            if (result.IsFaultInjected)
            {
                _totalFaults++;
            }

            if (result.IsDropped)
            {
                _totalDrops++;
            }

            Stations.FirstOrDefault(station => station.UnitId == result.UnitId)?.Record(args);
        });
    }

    private void OnClientConnected(object? sender, TransportConnectionEventArgs args)
    {
        App.Log.Write(ToolLogLevel.Information, "从站.通信",
            $"TCP 客户端已连接：{args.RemoteEndPoint}，连接 {args.ConnectionId}", (string?)null,
            context: SessionContext());
        _uiActions.Enqueue(() =>
        {
            if (Clients.All(client => client.ConnectionId != args.ConnectionId))
            {
                Clients.Add(new SlaveClientItem(args.ConnectionId, args.RemoteEndPoint));
            }
        });
    }

    private void OnClientDisconnected(object? sender, TransportConnectionEventArgs args)
    {
        App.Log.Write(ToolLogLevel.Information, "从站.通信",
            $"TCP 客户端已断开：{args.RemoteEndPoint}，连接 {args.ConnectionId}", (string?)null,
            context: SessionContext());
        _uiActions.Enqueue(() =>
        {
            SlaveClientItem? client = Clients.FirstOrDefault(item => item.ConnectionId == args.ConnectionId);
            if (client is not null)
            {
                Clients.Remove(client);
            }
        });
    }

    private void OnServiceError(object? sender, TransportErrorEventArgs args) => QueueError(args.Exception);
    private void OnSimulationError(Exception exception) => QueueError(exception);
    private void OnFrameSent(object? sender, TransportDataEventArgs args)
    {
        FrameSent?.Invoke(args);
        App.Log.Write(ToolLogLevel.Trace, "从站.报文", $"发送 {args.Data.Length} 字节", (string?)null,
            context: ToolLogContext.ForFrame(SessionId, args.Data.Span, args.ConnectionId is not null));
    }

    private void OnFrameReceived(object? sender, TransportDataEventArgs args)
    {
        FrameReceived?.Invoke(args);
        App.Log.Write(ToolLogLevel.Trace, "从站.报文", $"接收 {args.Data.Length} 字节", (string?)null,
            context: ToolLogContext.ForFrame(SessionId, args.Data.Span, args.ConnectionId is not null));
    }

    private void QueueError(Exception exception)
    {
        App.Log.Write(ToolLogLevel.Error, "从站.通信", exception.Message, exception, context: SessionContext());
        _uiActions.Enqueue(() =>
        {
            _totalErrors++;
            LastError = exception.Message;
            StatusMessage = $"通信/仿真错误：{exception.Message}";
        });
    }

    private string GetListenerSummary()
    {
        List<string> parts = [];
        if (UseTcp)
        {
            parts.Add($"TCP {TcpAddress}:{TcpPort}");
        }

        if (UseSerial)
        {
            string parity = Parity.Length > 0 ? Parity[0].ToString() : "?";
            string stopBits = StopBits switch
            {
                nameof(System.IO.Ports.StopBits.One) => "1",
                nameof(System.IO.Ports.StopBits.Two) => "2",
                nameof(System.IO.Ports.StopBits.OnePointFive) => "1.5",
                _ => "?"
            };
            parts.Add($"RTU {PortName} {BaudRate} {DataBits}{parity}{stopBits}");
        }

        return parts.Count == 0 ? "监听未启用" : string.Join(" | ", parts);
    }

    private string GetConnectionSummary()
    {
        List<string> parts = [];
        if (_service.IsTcpListening)
        {
            parts.Add($"TCP {_service.TcpEndPoint}");
        }

        if (_service.IsSerialOpen)
        {
            parts.Add($"RTU {PortName}");
        }

        return parts.Count == 0 ? "未监听" : string.Join("；", parts);
    }

    private string GetDefaultDataPath(byte unitId) => Path.Combine(
        Services.AppDataPathHelper.RuntimeCacheFolder,
        unitId == 1 ? "data.bin" : $"data-{unitId}.bin");

    private Encoding GetSelectedEncoding() => SelectedEncoding switch
    {
        "GB2312" => Encoding.GetEncoding(936),
        "UTF-8" => Encoding.UTF8,
        "Unicode" => Encoding.Unicode,
        "ASCII" => Encoding.ASCII,
        _ => throw new InvalidDataException("未知字符串编码。")
    };

    private static string MakeControlCharactersVisible(string value) => new(
        value.Select(character => char.IsControl(character) && !char.IsWhiteSpace(character) ? '�' : character).ToArray());

    private static bool ParseBit(string text) => text.Trim().ToUpperInvariant() switch
    {
        "1" or "ON" or "TRUE" => true,
        "0" or "OFF" or "FALSE" => false,
        _ => throw new InvalidDataException("线圈/离散输入值只能是 0/1、ON/OFF 或 TRUE/FALSE。")
    };

    private static ushort ParseUInt16(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static void ValidateRange(int startAddress, int count)
    {
        if (startAddress < 0 || startAddress >= ModbusDataStore.AreaSize
            || count < 1 || startAddress + count > ModbusDataStore.AreaSize)
        {
            throw new InvalidDataException("地址范围必须位于 0～65535，且至少包含一项。");
        }
    }

    private void RefreshPorts()
    {
        string current = PortName;
        PortNames.Clear();
        foreach (string port in SerialPort.GetPortNames().OrderBy(static port => port))
        {
            PortNames.Add(port);
        }

        if (PortNames.Contains(current))
        {
            PortName = current;
        }
        else if (PortNames.Count > 0)
        {
            PortName = PortNames[0];
        }
    }

    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            HandleCommandError(exception);
        }
    }

    private void HandleCommandError(Exception exception)
    {
        LastError = exception.Message;
        App.Log.Write(ToolLogLevel.Error, "从站.操作", exception.Message, exception);
        ErrorOccurred?.Invoke(exception.Message);
    }
}
