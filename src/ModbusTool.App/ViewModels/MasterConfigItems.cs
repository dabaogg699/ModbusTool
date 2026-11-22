// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;
using ModbusMaster;

namespace ModbusTool.App.ViewModels;

public sealed record ModbusFunctionOption(ModbusFunction Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class MasterBlockItem : ObservableObject
{
    private string _lastStatus = "未轮询";
    private double? _lastResponseMilliseconds;
    private DateTimeOffset? _lastUpdated;

    public MasterBlockItem(MasterReadBlock model) => Model = model;

    public MasterReadBlock Model { get; }

    public Guid Id => Model.Id;

    public string Name { get => Model.Name; set { Model.Name = value; OnPropertyChanged(); } }
    public ModbusFunction Function { get => Model.Function; set { Model.Function = value; OnPropertyChanged(); } }
    public ushort StartAddress { get => Model.StartAddress; set { Model.StartAddress = value; OnPropertyChanged(); } }
    public ushort Quantity { get => Model.Quantity; set { Model.Quantity = value; OnPropertyChanged(); } }

    public string LastStatus { get => _lastStatus; private set => SetProperty(ref _lastStatus, value); }
    public double? LastResponseMilliseconds { get => _lastResponseMilliseconds; private set => SetProperty(ref _lastResponseMilliseconds, value); }
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }

    public void SetResponse(TimeSpan elapsed, bool isException)
    {
        LastResponseMilliseconds = elapsed.TotalMilliseconds;
        LastUpdated = DateTimeOffset.Now;
        LastStatus = isException ? "从站异常" : "正常";
    }

    public void SetError(string message)
    {
        LastUpdated = DateTimeOffset.Now;
        LastStatus = message;
    }
}

public sealed class MasterPointItem : ObservableObject
{
    private string _rawValue = "-";
    private string _scaledValue = "-";
    private DateTimeOffset? _lastUpdated;

    public MasterPointItem(MasterDataPoint model) => Model = model;

    public MasterDataPoint Model { get; }

    public Guid Id => Model.Id;
    public string Name { get => Model.Name; set { Model.Name = value; OnPropertyChanged(); } }
    public ushort AddressOffset { get => Model.AddressOffset; set { Model.AddressOffset = value; OnPropertyChanged(); } }
    public ModbusDataType DataType { get => Model.DataType; set { Model.DataType = value; OnPropertyChanged(); } }
    public ModbusByteOrder ByteOrder { get => Model.ByteOrder; set { Model.ByteOrder = value; OnPropertyChanged(); } }
    public double Scale { get => Model.Scale; set { Model.Scale = value; OnPropertyChanged(); } }
    public double Offset { get => Model.Offset; set { Model.Offset = value; OnPropertyChanged(); } }
    public string Unit { get => Model.Unit; set { Model.Unit = value; OnPropertyChanged(); } }
    public bool HistoryEnabled
    {
        get => Model.HistoryEnabled;
        set
        {
            Model.HistoryEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurveEnabled));
        }
    }

    public bool CurveEnabled
    {
        get => Model.HistoryEnabled;
        set => HistoryEnabled = value;
    }

    public string GroupName { get => Model.GroupName; set { Model.GroupName = value; OnPropertyChanged(); } }
    public string Note { get => Model.Note; set { Model.Note = value; OnPropertyChanged(); } }
    public string RawValue { get => _rawValue; private set => SetProperty(ref _rawValue, value); }
    public string ScaledValue { get => _scaledValue; private set => SetProperty(ref _scaledValue, value); }
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }

    public void SetValue(MasterPointValue value)
    {
        RawValue = Convert.ToString(value.RawValue, CultureInfo.InvariantCulture) ?? "-";
        ScaledValue = value.ScaledValue.ToString("G8", CultureInfo.InvariantCulture)
            + (string.IsNullOrWhiteSpace(value.Unit) ? string.Empty : $" {value.Unit}");
        LastUpdated = value.Timestamp.ToLocalTime();
    }

    public void ClearValue()
    {
        RawValue = "-";
        ScaledValue = "-";
        LastUpdated = null;
    }
}
