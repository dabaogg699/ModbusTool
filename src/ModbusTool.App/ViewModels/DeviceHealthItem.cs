// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusTool.App.Controls;

namespace ModbusTool.App.ViewModels;

public sealed class DeviceHealthItem : ObservableObject
{
    private long _successes;
    private long _timeouts;
    private long _errors;
    private double _averageMilliseconds;
    private GridValueState _state = GridValueState.Unknown;
    private string _stateText = "未查询";
    private string _name = string.Empty;
    private byte _unitId;

    public required Guid DeviceId { get; init; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public byte UnitId { get => _unitId; set => SetProperty(ref _unitId, value); }

    public long Successes { get => _successes; private set => SetProperty(ref _successes, value); }

    public long Timeouts { get => _timeouts; private set => SetProperty(ref _timeouts, value); }

    public long Errors { get => _errors; private set => SetProperty(ref _errors, value); }

    public double AverageMilliseconds { get => _averageMilliseconds; private set => SetProperty(ref _averageMilliseconds, value); }

    public GridValueState State { get => _state; private set => SetProperty(ref _state, value); }

    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }

    public string SuccessRate
    {
        get
        {
            long total = Successes + Timeouts + Errors;
            return total == 0 ? "-" : $"{Successes * 100d / total:F1}%";
        }
    }

    public string AverageText => Successes == 0 ? "-" : $"{AverageMilliseconds:F1} ms";

    public void RecordSuccess(TimeSpan responseTime)
    {
        double milliseconds = responseTime.TotalMilliseconds;
        AverageMilliseconds = (AverageMilliseconds * Successes + milliseconds) / (Successes + 1);
        Successes++;
        State = GridValueState.Fresh;
        StateText = "正常";
        OnPropertyChanged(nameof(SuccessRate));
        OnPropertyChanged(nameof(AverageText));
    }

    public void RecordTimeout()
    {
        Timeouts++;
        State = GridValueState.Stale;
        StateText = "超时";
        OnPropertyChanged(nameof(SuccessRate));
    }

    public void RecordError()
    {
        Errors++;
        State = GridValueState.Error;
        StateText = "错误";
        OnPropertyChanged(nameof(SuccessRate));
    }
}
