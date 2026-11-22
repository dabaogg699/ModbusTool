// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.ObjectModel;
using ModbusMaster;

namespace ModbusTool.App.ViewModels;

public sealed class PointGroupItem : ObservableObject
{
    private bool _isExpanded;
    private bool _initialized;

    public PointGroupItem(MasterPointGroup model, Action<PointGroupItem, bool>? expandedChanged = null)
    {
        Model = model;
        _isExpanded = model.IsExpanded;
        ExpandedChanged = expandedChanged;
        _initialized = true;
    }

    public MasterPointGroup Model { get; }

    public Action<PointGroupItem, bool>? ExpandedChanged { get; }

    public string Name => Model.Name;

    public ObservableCollection<MasterPointItem> Points { get; } = [];

    public int PointCount => Points.Count;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
            {
                return;
            }

            Model.IsExpanded = value;
            if (_initialized)
            {
                ExpandedChanged?.Invoke(this, value);
            }
        }
    }

    public void NotifyCountChanged() => OnPropertyChanged(nameof(PointCount));
}
