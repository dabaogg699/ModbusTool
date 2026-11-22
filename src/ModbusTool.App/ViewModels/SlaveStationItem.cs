// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusDataModel;
using ModbusSlave;

namespace ModbusTool.App.ViewModels;

public sealed class SlaveAreaOption(ModbusDataArea value, string displayName)
{
    public ModbusDataArea Value { get; } = value;
    public string DisplayName { get; } = displayName;
    public override string ToString() => DisplayName;
}

public sealed class SlaveStationItem : ObservableObject
{
    private int _requestCount;
    private double _lastResponseMilliseconds;
    private string _lastStatus = "等待请求";

    public SlaveStationItem(byte unitId, ModbusDataStore store)
    {
        UnitId = unitId;
        Store = store;
    }

    public byte UnitId { get; }

    public string DisplayName => $"站号 {UnitId}";

    internal ModbusDataStore Store { get; }

    public int RequestCount { get => _requestCount; private set => SetProperty(ref _requestCount, value); }

    public double LastResponseMilliseconds
    {
        get => _lastResponseMilliseconds;
        private set => SetProperty(ref _lastResponseMilliseconds, value);
    }

    public string LastStatus { get => _lastStatus; private set => SetProperty(ref _lastStatus, value); }

    internal void Record(SlaveRequestEventArgs args)
    {
        RequestCount++;
        LastResponseMilliseconds = args.ProcessingTime.TotalMilliseconds;
        SlaveRequestResult result = args.Result;
        if (result.IsDropped)
        {
            LastStatus = "故障：丢包";
        }
        else if (result.Faults.HasFlag(SlaveFaultKind.InvalidCrc))
        {
            LastStatus = "故障：错误 CRC";
        }
        else if (result.Faults.HasFlag(SlaveFaultKind.ForcedException))
        {
            LastStatus = $"强制异常 {result.Response?.ExceptionCode}";
        }
        else if (result.Faults.HasFlag(SlaveFaultKind.InvalidValue))
        {
            LastStatus = "故障：错误值";
        }
        else if (result.Response?.IsException == true)
        {
            LastStatus = $"异常 {result.Response.ExceptionCode}";
        }
        else if (result.Error is not null)
        {
            LastStatus = "处理失败";
        }
        else if (result.IsBroadcast)
        {
            LastStatus = "广播写已处理";
        }
        else
        {
            LastStatus = "正常";
        }
    }
}

public sealed record SlaveClientItem(string ConnectionId, string? RemoteEndPoint)
{
    public string DisplayName => $"{RemoteEndPoint ?? "未知端点"} ({ConnectionId})";
}
