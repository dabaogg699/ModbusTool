// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;

namespace ModbusTool.App.Controls;

public enum PacketDirection
{
    Sent,
    Received
}

public enum PacketCrcStatus
{
    NotApplicable,
    Valid,
    Mismatch,
    Malformed
}

public sealed record PacketEntry(
    DateTimeOffset Timestamp,
    PacketDirection Direction,
    byte? UnitId,
    byte? Function,
    string? ConnectionId,
    bool IsTcp,
    byte[] Data)
{
    public string DirectionText => Direction == PacketDirection.Sent ? "发送" : "接收";
    public string? ExchangeOperation { get; init; }
    public string OperationText => ExchangeOperation ?? DirectionText;
    public string UnitText => UnitId?.ToString(CultureInfo.InvariantCulture) ?? "-";
    public string FunctionText => Function?.ToString("X2", CultureInfo.InvariantCulture) ?? "-";
    public bool IsException => Function.HasValue && (Function.Value & 0x80) != 0;
    public PacketCrcStatus CrcStatus { get; init; }
    public bool HasCrcError => CrcStatus is PacketCrcStatus.Mismatch or PacketCrcStatus.Malformed;
    public string CrcText => CrcStatus switch
    {
        PacketCrcStatus.Valid => "正常",
        PacketCrcStatus.Mismatch => "CRC 不匹配",
        PacketCrcStatus.Malformed => "帧不完整",
        _ => "—"
    };

    public int Length => Data.Length;
    public string HexData => string.Join(" ", Convert.ToHexString(Data).Chunk(2).Select(part => new string(part)));
    public string SummaryLine =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}\t{OperationText}\t站号:{UnitText}\t功能码:{FunctionText}\tCRC:{CrcText}\t{HexData}";
}

public partial class PacketMonitor : UserControl
{
    public static readonly DependencyProperty IsTcpProperty = DependencyProperty.Register(
        nameof(IsTcp), typeof(bool), typeof(PacketMonitor), new PropertyMetadata(false, OnModeChanged));
    public static readonly DependencyProperty AutoDetectProperty = DependencyProperty.Register(
        nameof(AutoDetect), typeof(bool), typeof(PacketMonitor), new PropertyMetadata(false, OnModeChanged));
    public static readonly DependencyProperty IsMasterProperty = DependencyProperty.Register(
        nameof(IsMaster), typeof(bool), typeof(PacketMonitor), new PropertyMetadata(false, OnModeChanged));
    public static readonly DependencyProperty MaxEntriesProperty = DependencyProperty.Register(
        nameof(MaxEntries), typeof(int), typeof(PacketMonitor), new PropertyMetadata(5000, OnMaxEntriesPropertyChanged));

    private readonly ObservableCollection<PacketEntry> _entries = [];
    private readonly ConcurrentQueue<PacketEntry> _pending = new();
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _refreshTimer;
    private volatile bool _isPaused;
    // 通信线程会调用 AddFrame，依赖属性只能在 UI 线程访问，因此在此缓存一份普通字段。
    private volatile bool _isTcpMode;
    private volatile bool _autoDetectMode;
    private volatile bool _isMasterMode;
    private volatile int _maxEntries = 5000;
    private StreamWriter? _overflowWriter;
    private string? _overflowPath;

    public PacketMonitor()
    {
        InitializeComponent();
        _isTcpMode = IsTcp;
        _autoDetectMode = AutoDetect;
        _isMasterMode = IsMaster;
        _maxEntries = MaxEntries;
        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = FilterEntry;
        Grid.ItemsSource = _view;
        Grid.PreviewMouseRightButtonDown += OnGridRightButtonDown;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += OnRefreshTick;
        // 报文标签未显示时也持续消化缓存，避免长时间轮询导致后台队列无限增长。
        _refreshTimer.Start();
        Unloaded += (_, _) => FlushOverflowWriter();
        UpdateStatus();
    }

    private static void OnModeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        PacketMonitor monitor = (PacketMonitor)sender;
        monitor._isTcpMode = monitor.IsTcp;
        monitor._autoDetectMode = monitor.AutoDetect;
        monitor._isMasterMode = monitor.IsMaster;
    }

    private static void OnMaxEntriesPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((PacketMonitor)sender)._maxEntries = (int)args.NewValue;
    }

    public event EventHandler? PacketCountChanged;

    public int EntryCount => _entries.Count;

    public DataGrid PacketGrid => Grid;

    public bool IsTcp
    {
        get => (bool)GetValue(IsTcpProperty);
        set => SetValue(IsTcpProperty, value);
    }

    public bool AutoDetect
    {
        get => (bool)GetValue(AutoDetectProperty);
        set => SetValue(AutoDetectProperty, value);
    }

    public bool IsMaster
    {
        get => (bool)GetValue(IsMasterProperty);
        set => SetValue(IsMasterProperty, value);
    }

    public int MaxEntries
    {
        get => (int)GetValue(MaxEntriesProperty);
        set => SetValue(MaxEntriesProperty, Math.Max(100, value));
    }

    public void AddSent(TransportDataEventArgs args) => AddFrame(PacketDirection.Sent, args.Data, args.Timestamp, args.ConnectionId);

    public void AddReceived(TransportDataEventArgs args) => AddFrame(PacketDirection.Received, args.Data, args.Timestamp, args.ConnectionId);

    public void AddFrame(PacketDirection direction, ReadOnlyMemory<byte> data, DateTimeOffset? timestamp = null, string? connectionId = null)
    {
        if (_isPaused || data.IsEmpty)
        {
            return;
        }

        ReadOnlySpan<byte> span = data.Span;
        bool isTcp = _isTcpMode || (_autoDetectMode && LooksLikeTcpFrame(span));
        int offset = isTcp && span.Length >= 8 ? 6 : 0;
        byte? unitId = span.Length > offset ? span[offset] : null;
        byte? function = span.Length > offset + 1 ? span[offset + 1] : null;
        string? operation = null;
        if (function.HasValue && (function.Value & 0x7F) is 5 or 6 or 15 or 16)
        {
            bool isRequest = _isMasterMode ? direction == PacketDirection.Sent : direction == PacketDirection.Received;
            operation = isRequest ? "写入请求" : "写入应答";
        }

        PacketEntry entry = new(timestamp ?? DateTimeOffset.Now, direction, unitId, function, connectionId, isTcp, data.ToArray())
        {
            CrcStatus = EvaluateCrc(isTcp, span),
            ExchangeOperation = operation
        };

        while (_pending.Count >= _maxEntries && _pending.TryDequeue(out _))
        {
        }

        _pending.Enqueue(entry);
    }

    private static PacketCrcStatus EvaluateCrc(bool isTcp, ReadOnlySpan<byte> frame)
    {
        if (isTcp)
        {
            return PacketCrcStatus.NotApplicable;
        }

        if (frame.Length < 4)
        {
            return PacketCrcStatus.Malformed;
        }

        return ModbusCrc16.Validate(frame) ? PacketCrcStatus.Valid : PacketCrcStatus.Mismatch;
    }

    private static bool LooksLikeTcpFrame(ReadOnlySpan<byte> data)
    {
        return data.Length >= 8 && data[2] == 0 && data[3] == 0
            && BinaryPrimitives.ReadUInt16BigEndian(data[4..]) == data.Length - 6;
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        _entries.Clear();
        FlushOverflowWriter();
        _overflowPath = null;
        UpdateStatus();
    }

    private void OnMaxEntriesChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MaxEntriesBox.Text, out int value))
        {
            MaxEntries = value;
            MaxEntriesBox.Text = MaxEntries.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            MaxEntriesBox.Text = MaxEntries.ToString(CultureInfo.InvariantCulture);
        }

        UpdateStatus();
    }

    private void AppendOverflow(PacketEntry entry)
    {
        try
        {
            if (_overflowWriter is null)
            {
                string directory = Services.AppDataPathHelper.LogsFolder;
                Directory.CreateDirectory(directory);
                _overflowPath = Path.Combine(directory, $"PacketLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                _overflowWriter = new StreamWriter(_overflowPath, false, new UTF8Encoding(true));
                _overflowWriter.WriteLine("# Modbus 报文超限自动落盘（按时间戳命名）");
            }

            _overflowWriter.WriteLine(entry.SummaryLine);
            _overflowWriter.Flush();
        }
        catch (Exception exception)
        {
            AutoExportBox.IsChecked = false;
            MessageBox.Show(Window.GetWindow(this), exception.Message, "报文自动落盘失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FlushOverflowWriter()
    {
        try
        {
            _overflowWriter?.Flush();
        }
        finally
        {
            _overflowWriter?.Dispose();
            _overflowWriter = null;
        }
    }

    private bool FilterEntry(object item)
    {
        if (item is not PacketEntry entry)
        {
            return false;
        }

        if (DirectionFilterBox.SelectedIndex == 1 && entry.Direction != PacketDirection.Sent)
        {
            return false;
        }

        if (DirectionFilterBox.SelectedIndex == 2 && entry.Direction != PacketDirection.Received)
        {
            return false;
        }

        if (CrcFilterBox.SelectedIndex == 1 && entry.CrcStatus != PacketCrcStatus.Mismatch)
        {
            return false;
        }

        if (CrcFilterBox.SelectedIndex == 2 && entry.CrcStatus != PacketCrcStatus.Malformed)
        {
            return false;
        }

        string unitFilter = UnitFilterBox.Text.Trim();
        if (unitFilter.Length > 0)
        {
            if (!byte.TryParse(unitFilter, out byte unit) || entry.UnitId != unit)
            {
                return false;
            }
        }

        string functionFilter = FunctionFilterBox.Text.Trim();
        if (functionFilter.Length > 0)
        {
            if (functionFilter.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                functionFilter = functionFilter[2..];
            }

            if (!byte.TryParse(functionFilter, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte function)
                || entry.Function != function)
            {
                return false;
            }
        }

        return true;
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        bool changed = false;
        for (int index = 0; index < 1000 && _pending.TryDequeue(out PacketEntry? entry); index++)
        {
            _entries.Add(entry);
            changed = true;
        }

        while (_entries.Count > _maxEntries)
        {
            PacketEntry evicted = _entries[0];
            _entries.RemoveAt(0);
            if (AutoExportBox.IsChecked == true)
            {
                AppendOverflow(evicted);
            }
        }

        if (changed)
        {
            UpdateStatus();
            PacketCountChanged?.Invoke(this, EventArgs.Empty);
            if (_view.Cast<object>().Any())
            {
                Grid.ScrollIntoView(_entries[^1]);
            }
        }
    }

    private void OnPauseChanged(object sender, RoutedEventArgs e)
    {
        _isPaused = PauseButton.IsChecked == true;
        PauseButton.Content = _isPaused ? "继续" : "暂停";
        UpdateStatus();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateStatus();
    }

    private void OnDirectionFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateStatus();
    }

    private void OnGridRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    private void OnCopyPacketClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is PacketEntry entry)
        {
            CopyToClipboard(entry.SummaryLine);
        }
    }

    private void OnCopyHexClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is PacketEntry entry)
        {
            CopyToClipboard(entry.HexData);
        }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = "已复制当前报文到剪贴板";
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "复制报文失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => Clear();

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出报文日志",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"ModbusPackets_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = _view.Cast<PacketEntry>().Select(entry => entry.SummaryLine).ToArray();
        await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(true));
    }

    private void UpdateStatus()
    {
        if (StatusText is not null)
        {
            StatusText.Text = $"缓存 {_entries.Count}/{MaxEntries} 条，当前显示 {_view?.Cast<object>().Count() ?? 0} 条"
                + (_isPaused ? "，已暂停采集" : string.Empty)
                + (_overflowPath is null ? string.Empty : $"，超限落盘：{_overflowPath}");
        }

        if (EmptyState is not null)
        {
            EmptyState.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
