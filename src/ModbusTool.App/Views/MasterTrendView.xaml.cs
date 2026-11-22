// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using System.Windows.Controls;
using ModbusTool.App.Controls;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

public partial class MasterTrendView : UserControl
{
    private readonly HashSet<Guid> _series = [];
    private MasterViewModel? _viewModel;
    private TrendChart? _chart;

    public MasterTrendView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.TrendSampleAvailable -= OnTrendSampleAvailable;
            _viewModel.TrendSeriesChanged -= RebuildTrendSeries;
        }

        _viewModel = e.NewValue as MasterViewModel;
        if (_viewModel is not null)
        {
            _viewModel.TrendSampleAvailable += OnTrendSampleAvailable;
            _viewModel.TrendSeriesChanged += RebuildTrendSeries;
        }

        RebuildTrendSeries();
    }

    private void OnTrendChartLoaded(object sender, RoutedEventArgs e)
    {
        if (_chart is not null)
        {
            _chart.SeriesStyleChanged -= OnSeriesStyleChanged;
        }

        _chart = (TrendChart)sender;
        _chart.SeriesStyleChanged += OnSeriesStyleChanged;
        RebuildTrendSeries();
    }

    private void OnSeriesStyleChanged(object? sender, Guid pointId)
    {
        if (_chart is null || _viewModel is null)
        {
            return;
        }

        TrendChart.TrendSeriesStyleValue? current = _chart.GetSeriesStyles()
            .FirstOrDefault(style => style.Id == pointId);
        if (current is null)
        {
            return;
        }

        List<ModbusMaster.TrendSeriesStyle> styles = _viewModel.Workspace.TrendSeriesStyles;
        ModbusMaster.TrendSeriesStyle? entry = styles.FirstOrDefault(style => style.PointId == pointId);
        if (entry is null)
        {
            entry = new ModbusMaster.TrendSeriesStyle { PointId = pointId };
            styles.Add(entry);
        }

        entry.Color = current.Color;
        entry.LineWidth = current.LineWidth;
        entry.LinePattern = current.Pattern;
        _viewModel.NotifyTrendStyleChanged();
    }

    private void OnTrendSampleAvailable(Guid pointId, ModbusMaster.TrendSample sample)
    {
        _chart?.Enqueue(new TrendChartPoint(pointId, sample.Timestamp, sample.Value, sample.IsBreak));
    }

    private void RebuildTrendSeries()
    {
        if (_chart is null || _viewModel is null)
        {
            return;
        }

        Dictionary<Guid, MasterPointItem> desired = _viewModel.HistoryPoints.ToDictionary(point => point.Id);
        foreach (Guid id in _series.Where(id => !desired.ContainsKey(id)).ToArray())
        {
            _chart.RemoveSeries(id);
            _series.Remove(id);
        }

        foreach (MasterPointItem point in desired.Values)
        {
            // 不再“先删再建”：删除会让调色板索引重新分配导致撞色，并丢失图例隐藏状态。
            // 样式与快照刷新保持原有语义，趋势服务被重置（快照为空）时曲线随之清空。
            _series.Add(point.Id);
            _chart.AddSeries(point.Id, point.Name);
            if (_viewModel.Workspace.TrendSeriesStyles
                .FirstOrDefault(style => style.PointId == point.Id) is { } saved)
            {
                _chart.ApplySeriesStyle(point.Id, saved.Color, saved.LineWidth, saved.LinePattern);
            }

            _chart.SetSamples(point.Id, _viewModel.GetTrendSnapshot(point.Id)
                .Select(sample => new TrendChartPoint(point.Id, sample.Timestamp, sample.Value, sample.IsBreak)));
        }
    }
}
