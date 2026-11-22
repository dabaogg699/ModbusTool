// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ModbusTool.App.Themes;
using ModbusTool.App.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;
using PlotColor = ScottPlot.Color;

namespace ModbusTool.App.Controls;

public sealed record TrendChartPoint(Guid SeriesId, DateTimeOffset Timestamp, double? Value, bool IsBreak = false);

public partial class TrendChart : UserControl
{
    private static readonly string[] PaletteHex =
    [
        "#2563EB", "#DC2626", "#059669", "#D97706", "#7C3AED", "#0891B2"
    ];

    public sealed record TrendSeriesStyleValue(Guid Id, string Color, double LineWidth, string Pattern);

    public event EventHandler<Guid>? SeriesStyleChanged;

    public static readonly DependencyProperty ExportCommandProperty = DependencyProperty.Register(
        nameof(ExportCommand), typeof(ICommand), typeof(TrendChart), new PropertyMetadata(null));

    private const int MaxPointsPerSeries = 5000;
    private const int MaxPendingPoints = 5000;
    private readonly Dictionary<Guid, SeriesState> _series = [];
    private readonly ObservableCollection<TrendLegendItem> _legendItems = [];
    private readonly ConcurrentQueue<TrendChartPoint> _pending = new();
    private readonly DispatcherTimer _refreshTimer;
    private int _pendingCount;
    private bool _autoScale;

    // 采样来自通信线程，只能读取普通字段；PauseButton 等 WPF 控件仅允许 UI 线程访问。
    private volatile bool _isPaused;

    public TrendChart()
    {
        InitializeComponent();
        LegendItems.ItemsSource = _legendItems;
        Plot plot = PlotView.Plot;
        // ScottPlot 默认字体不含中文字形，显式指定系统中文字体，避免坐标轴标题显示为方框。
        plot.Font.Set("Microsoft YaHei");
        plot.HideLegend();
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
        {
            LabelFormatter = value => value.ToString("HH:mm:ss"),
            MaxTickCount = 8
        };
        plot.Axes.Bottom.Label.Text = "时间";
        plot.Axes.Left.Label.Text = "数值";
        ApplyAxisFont();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += OnRefreshTick;
        _autoScale = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyPlotTheme();
        UpdateEmptyState();
    }

    public ICommand? ExportCommand
    {
        get => (ICommand?)GetValue(ExportCommandProperty);
        set => SetValue(ExportCommandProperty, value);
    }

    public void AddSeries(Guid id, string title)
    {
        if (_series.TryGetValue(id, out SeriesState? existing))
        {
            existing.Title = title;
            existing.Legend.Title = title;
            foreach (Scatter plottable in existing.Plottables)
            {
                plottable.LegendText = title;
            }

            return;
        }

        // 取最小空闲调色板索引：若按 _series.Count 分配，删除后重建会让多条曲线拿到同一索引而撞色。
        HashSet<int> usedPalette = _series.Values.Select(series => series.PaletteIndex).ToHashSet();
        int paletteIndex = 0;
        while (usedPalette.Contains(paletteIndex))
        {
            paletteIndex++;
        }

        string hex = PaletteHex[paletteIndex % PaletteHex.Length];
        PlotColor color = PlotColor.FromHex(hex);
        TrendLegendItem legend = new()
        {
            Id = id,
            Title = title,
            ColorBrush = CreateBrush(color),
            IsVisible = true
        };
        SeriesState state = new()
        {
            Id = id,
            Title = title,
            Color = color,
            Hex = hex,
            PaletteIndex = paletteIndex,
            LineWidth = 1.7f,
            Pattern = LinePattern.Solid,
            PatternName = "Solid",
            Legend = legend
        };
        _series.Add(id, state);
        _legendItems.Add(legend);
        Rebuild(state);
        LegendScroll.Visibility = Visibility.Visible;
        UpdateEmptyState();
        PlotView.Refresh();
    }

    public bool RemoveSeries(Guid id)
    {
        if (!_series.Remove(id, out SeriesState? state))
        {
            return false;
        }

        RemovePlottables(state);
        _legendItems.Remove(state.Legend);
        LegendScroll.Visibility = _legendItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HideTracker();
        UpdateEmptyState();
        PlotView.Refresh();
        return true;
    }

    public void Enqueue(TrendChartPoint point)
    {
        if (_isPaused)
        {
            return;
        }

        _pending.Enqueue(point);
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingPoints)
        {
            TryDequeuePending(out _);
        }
    }

    public void SetSamples(Guid seriesId, IEnumerable<TrendChartPoint> points)
    {
        if (!_series.TryGetValue(seriesId, out SeriesState? state))
        {
            throw new InvalidOperationException("Add the trend series before loading samples.");
        }

        state.Points.Clear();
        foreach (TrendChartPoint point in points.TakeLast(MaxPointsPerSeries))
        {
            state.Points.Add(ToPoint(point));
        }

        Rebuild(state);
        UpdateLatestValue(state);
        UpdateEmptyState();
        RefreshWithScale();
    }

    public void Clear(Guid? seriesId = null)
    {
        IEnumerable<SeriesState> targets = seriesId.HasValue && _series.TryGetValue(seriesId.Value, out SeriesState? selected)
            ? [selected]
            : _series.Values;
        foreach (SeriesState state in targets)
        {
            state.Points.Clear();
            Rebuild(state);
            UpdateLatestValue(state);
        }

        HideTracker();
        UpdateEmptyState();
        PlotView.Refresh();
    }

    public IReadOnlyList<TrendSeriesStyleValue> GetSeriesStyles() =>
        _series.Values
            .Select(state => new TrendSeriesStyleValue(state.Id, state.Hex, state.LineWidth, state.PatternName))
            .ToList();

    public void ApplySeriesStyle(Guid id, string colorHex, double lineWidth, string patternName)
    {
        if (!_series.TryGetValue(id, out SeriesState? state))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            state.Hex = colorHex;
            state.Color = PlotColor.FromHex(colorHex);
            state.Legend.ColorBrush = CreateBrush(state.Color);
        }

        if (lineWidth > 0)
        {
            state.LineWidth = (float)lineWidth;
        }

        if (!string.IsNullOrWhiteSpace(patternName))
        {
            state.PatternName = patternName;
            state.Pattern = ToPattern(patternName);
        }

        Rebuild(state);
        PlotView.Refresh();
    }

    public void ResetSeriesStyle(Guid id)
    {
        if (!_series.TryGetValue(id, out SeriesState? state))
        {
            return;
        }

        ApplyDefaultStyle(state);
        Rebuild(state);
        PlotView.Refresh();
        SeriesStyleChanged?.Invoke(this, id);
    }

    public void ResetAllStyles()
    {
        foreach (SeriesState state in _series.Values)
        {
            ApplyDefaultStyle(state);
            Rebuild(state);
        }

        PlotView.Refresh();
        foreach (Guid id in _series.Keys)
        {
            SeriesStyleChanged?.Invoke(this, id);
        }
    }

    private static void ApplyDefaultStyle(SeriesState state)
    {
        state.Hex = PaletteHex[state.PaletteIndex % PaletteHex.Length];
        state.Color = PlotColor.FromHex(state.Hex);
        state.LineWidth = 1.7f;
        state.Pattern = LinePattern.Solid;
        state.PatternName = "Solid";
        state.Legend.ColorBrush = CreateBrush(state.Color);
    }

    private static LinePattern ToPattern(string name) => name switch
    {
        "Dashed" => LinePattern.Dashed,
        "Dotted" => LinePattern.Dotted,
        _ => LinePattern.Solid
    };

    private void Rebuild(SeriesState state)
    {
        RemovePlottables(state);
        List<double> xs = [];
        List<double> ys = [];
        foreach ((double x, double y, bool isBreak) in state.Points)
        {
            if (isBreak || double.IsNaN(y))
            {
                Flush();
                continue;
            }

            xs.Add(x);
            ys.Add(y);
        }

        Flush();
        return;

        void Flush()
        {
            if (xs.Count == 0)
            {
                return;
            }

            Scatter scatter = PlotView.Plot.Add.Scatter(xs.ToArray(), ys.ToArray(), state.Color);
            scatter.LineWidth = state.LineWidth;
            scatter.LinePattern = state.Pattern;
            scatter.MarkerSize = 0;
            scatter.LegendText = state.Title;
            scatter.IsVisible = state.Legend.IsVisible;
            state.Plottables.Add(scatter);
            xs = [];
            ys = [];
        }
    }

    private void RemovePlottables(SeriesState state)
    {
        foreach (Scatter plottable in state.Plottables)
        {
            PlotView.Plot.Remove(plottable);
        }

        state.Plottables.Clear();
    }

    private void UpdateLatestValue(SeriesState state)
    {
        state.Legend.Value = "—";
        for (int index = state.Points.Count - 1; index >= 0; index--)
        {
            (double _, double y, bool isBreak) = state.Points[index];
            if (!isBreak && !double.IsNaN(y))
            {
                state.Legend.Value = y.ToString("0.######", CultureInfo.InvariantCulture);
                break;
            }
        }
    }

    private void UpdateEmptyState()
    {
        bool hasData = _series.Values.Any(
            state => state.Points.Any(point => !point.IsBreak && !double.IsNaN(point.Y)));
        EmptyState.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        if (!hasData)
        {
            HideTracker();
        }
    }

    private static (double X, double Y, bool IsBreak) ToPoint(TrendChartPoint point)
    {
        // 采样时间戳为 UTC，必须转为本地时间，否则时间轴与十字准星会比系统时间慢 8 小时。
        double x = point.Timestamp.LocalDateTime.ToOADate();
        return point.IsBreak || !point.Value.HasValue
            ? (x, double.NaN, true)
            : (x, point.Value.Value, false);
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        try
        {
            HashSet<Guid> changed = [];
            for (int index = 0; index < 3000 && TryDequeuePending(out TrendChartPoint? point); index++)
            {
                if (_series.TryGetValue(point.SeriesId, out SeriesState? state))
                {
                    state.Points.Add(ToPoint(point));
                    changed.Add(point.SeriesId);
                }
            }

            if (changed.Count == 0)
            {
                return;
            }

            foreach (Guid id in changed)
            {
                SeriesState state = _series[id];
                if (state.Points.Count > MaxPointsPerSeries)
                {
                    state.Points.RemoveRange(0, state.Points.Count - MaxPointsPerSeries);
                }

                Rebuild(state);
                UpdateLatestValue(state);
            }

            UpdateEmptyState();
            RefreshWithScale();
        }
        catch (Exception exception)
        {
            // 单个异常点或渲染异常不得中断后续采样刷新。
            App.Log.Write(
                ModbusCore.Common.ToolLogLevel.Warning,
                "趋势",
                $"趋势刷新失败：{exception.Message}");
        }
    }

    private void RefreshWithScale()
    {
        if (_autoScale)
        {
            PlotView.Plot.Axes.AutoScale();
        }

        PlotView.Refresh();
    }

    private bool TryDequeuePending([NotNullWhen(true)] out TrendChartPoint? point)
    {
        if (!_pending.TryDequeue(out point))
        {
            return false;
        }

        Interlocked.Decrement(ref _pendingCount);
        return true;
    }

    private void OnLegendVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: Guid id } checkBox && _series.TryGetValue(id, out SeriesState? state))
        {
            bool visible = checkBox.IsChecked == true;
            foreach (Scatter plottable in state.Plottables)
            {
                plottable.IsVisible = visible;
            }

            HideTracker();
            PlotView.Refresh();
        }
    }

    private void OnStyleColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string hex } && GetLegend(sender) is { } legend
            && _series.TryGetValue(legend.Id, out SeriesState? state))
        {
            state.Hex = hex;
            state.Color = PlotColor.FromHex(hex);
            state.Legend.ColorBrush = CreateBrush(state.Color);
            Rebuild(state);
            HideTracker();
            PlotView.Refresh();
            SeriesStyleChanged?.Invoke(this, legend.Id);
        }
    }

    private void OnStyleWidthClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string text } && GetLegend(sender) is { } legend
            && _series.TryGetValue(legend.Id, out SeriesState? state)
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float width))
        {
            state.LineWidth = width;
            Rebuild(state);
            PlotView.Refresh();
            SeriesStyleChanged?.Invoke(this, legend.Id);
        }
    }

    private void OnStylePatternClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string text } && GetLegend(sender) is { } legend
            && _series.TryGetValue(legend.Id, out SeriesState? state))
        {
            state.PatternName = text;
            state.Pattern = ToPattern(text);
            Rebuild(state);
            PlotView.Refresh();
            SeriesStyleChanged?.Invoke(this, legend.Id);
        }
    }

    private void OnStyleResetClick(object sender, RoutedEventArgs e)
    {
        if (GetLegend(sender) is { } legend)
        {
            ResetSeriesStyle(legend.Id);
        }
    }

    private void OnStyleResetAllClick(object sender, RoutedEventArgs e) => ResetAllStyles();

    private static TrendLegendItem? GetLegend(object sender) =>
        (sender as FrameworkElement)?.DataContext as TrendLegendItem;

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(PlotHost);
        List<TrendTrackerValue> readings = [];
        double cursorX = double.NaN;
        try
        {
            Coordinates coordinates = PlotView.Plot.GetCoordinates(
                new Pixel((float)position.X, (float)position.Y),
                PlotView.Plot.Axes.Bottom,
                PlotView.Plot.Axes.Left);
            cursorX = coordinates.X;
        }
        catch (Exception)
        {
            HideTracker();
            return;
        }

        if (double.IsNaN(cursorX) || double.IsInfinity(cursorX))
        {
            HideTracker();
            return;
        }

        foreach (SeriesState state in _series.Values)
        {
            if (!state.Legend.IsVisible || FindNearest(state.Points, cursorX) is not { } nearest)
            {
                continue;
            }

            readings.Add(new TrendTrackerValue(
                state.Title,
                nearest.Y.ToString("0.######", CultureInfo.InvariantCulture),
                state.Legend.ColorBrush));
        }

        if (readings.Count == 0)
        {
            HideTracker();
            return;
        }

        TrackerTime.Text = $"时间 {DateTime.FromOADate(cursorX):HH:mm:ss.fff}";
        TrackerValues.ItemsSource = readings;
        CrosshairLine.X1 = position.X;
        CrosshairLine.X2 = position.X;
        CrosshairLine.Y1 = 0;
        CrosshairLine.Y2 = PlotHost.ActualHeight;
        CrosshairLine.Visibility = Visibility.Visible;

        TrackerBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size desired = TrackerBorder.DesiredSize;
        double left = position.X + 14;
        if (left + desired.Width > PlotHost.ActualWidth - 4)
        {
            left = position.X - desired.Width - 14;
        }

        double top = Math.Clamp(position.Y + 14, 4, Math.Max(4, PlotHost.ActualHeight - desired.Height - 4));
        Canvas.SetLeft(TrackerBorder, Math.Max(4, left));
        Canvas.SetTop(TrackerBorder, top);
        TrackerBorder.Visibility = Visibility.Visible;
    }

    private static (double X, double Y, bool IsBreak)? FindNearest(
        List<(double X, double Y, bool IsBreak)> points,
        double x)
    {
        int low = 0;
        int high = points.Count - 1;
        int best = -1;
        double bestDistance = double.MaxValue;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            double distance = Math.Abs(points[middle].X - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = middle;
            }

            if (points[middle].X < x)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (best < 0)
        {
            return null;
        }

        // 向两侧寻找最近的有效点，避免落在断点上返回空值。
        for (int offset = 0; offset < points.Count; offset++)
        {
            int forward = best + offset;
            if (forward < points.Count && !points[forward].IsBreak && !double.IsNaN(points[forward].Y))
            {
                return points[forward];
            }

            int backward = best - offset;
            if (backward >= 0 && !points[backward].IsBreak && !double.IsNaN(points[backward].Y))
            {
                return points[backward];
            }
        }

        return null;
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e) => HideTracker();

    private void OnPlotUserZoom(object sender, MouseWheelEventArgs e) => _autoScale = false;

    private void OnPlotUserPan(object sender, MouseButtonEventArgs e) => _autoScale = false;

    private void HideTracker()
    {
        CrosshairLine.Visibility = Visibility.Collapsed;
        TrackerBorder.Visibility = Visibility.Collapsed;
        TrackerValues.ItemsSource = null;
    }

    private static SolidColorBrush CreateBrush(PlotColor color)
    {
        SolidColorBrush brush = new(System.Windows.Media.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => Clear();

    private void OnPauseChanged(object sender, RoutedEventArgs e)
    {
        _isPaused = PauseButton.IsChecked == true;
        PauseButton.Content = _isPaused ? "继续更新" : "暂停更新";
        if (!_isPaused)
        {
            RefreshWithScale();
        }
    }

    private void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "保存趋势快照",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"ModbusTrend_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            AddExtension = true,
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            int width = Math.Max(640, (int)PlotView.ActualWidth);
            int height = Math.Max(360, (int)PlotView.ActualHeight);
            RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(PlotView);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            MessageBox.Show(Window.GetWindow(this), $"趋势快照已保存：\n{dialog.FileName}", "快照",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "保存快照失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        HideTracker();
        _autoScale = true;
        PlotView.Plot.Axes.AutoScale();
        PlotView.Refresh();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyPlotTheme();
        ThemeManager.ThemeChanged += OnThemeChanged;
        _refreshTimer.Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _refreshTimer.Stop();
        HideTracker();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyPlotTheme();

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        HideTracker();
        while (TryDequeuePending(out _))
        {
        }

        Interlocked.Exchange(ref _pendingCount, 0);
        foreach (SeriesState state in _series.Values)
        {
            RemovePlottables(state);
        }

        _series.Clear();
        _legendItems.Clear();
        LegendItems.ItemsSource = null;
    }

    private void ApplyAxisFont()
    {
        const string fontName = "Microsoft YaHei";
#if !NET6_0
        SkiaSharp.SKTypeface? typeface = SkiaSharp.SKFontManager.Default.MatchFamily(fontName);
#endif
        if (PlotView.Plot.Axes.Bottom is ScottPlot.AxisPanels.AxisBase bottom)
        {
            bottom.LabelStyle.FontName = fontName;
            bottom.TickLabelStyle.FontName = fontName;
#if !NET6_0
            if (typeface is not null)
            {
                bottom.LabelStyle.Font = typeface;
            }
#endif
        }

        if (PlotView.Plot.Axes.Left is ScottPlot.AxisPanels.AxisBase left)
        {
            left.LabelStyle.FontName = fontName;
            left.TickLabelStyle.FontName = fontName;
#if !NET6_0
            if (typeface is not null)
            {
                left.LabelStyle.Font = typeface;
            }
#endif
        }
    }

    private void ApplyPlotTheme()
    {
        bool dark = ThemeManager.IsDark;
        PlotColor text = dark ? PlotColor.FromHex("#E2E8F0") : PlotColor.FromHex("#334155");
        PlotColor grid = dark ? PlotColor.FromHex("#334155") : PlotColor.FromHex("#E2E8F0");
        PlotColor background = dark ? PlotColor.FromHex("#0F172A") : PlotColor.FromHex("#FFFFFF");
        Plot plot = PlotView.Plot;
        plot.FigureBackground.Color = background;
        plot.DataBackground.Color = background;
        plot.Grid.MajorLineColor = grid;
        plot.Grid.MinorLineColor = grid;
        plot.Axes.Color(text);
        plot.Axes.FrameColor(grid);
        PlotView.Refresh();
    }

    private sealed class SeriesState
    {
        public required Guid Id { get; init; }
        public required string Title { get; set; }
        public required PlotColor Color { get; set; }
        public required string Hex { get; set; }
        public required int PaletteIndex { get; init; }
        public required string PatternName { get; set; }
        public required float LineWidth { get; set; }
        public required LinePattern Pattern { get; set; }
        public required TrendLegendItem Legend { get; set; }
        public List<(double X, double Y, bool IsBreak)> Points { get; } = [];
        public List<Scatter> Plottables { get; } = [];
    }
}

public sealed class TrendLegendItem : ObservableObject
{
    private string _title = string.Empty;
    private string _value = "—";
    private Brush _colorBrush = Brushes.White;

    public required Guid Id { get; init; }
    public bool IsVisible { get; set; }

    public Brush ColorBrush
    {
        get => _colorBrush;
        set => SetProperty(ref _colorBrush, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

public sealed record TrendTrackerValue(string Title, string Value, Brush ColorBrush);
