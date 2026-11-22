// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ModbusCore.Common;

namespace ModbusTool.App.Controls;

public partial class LogView : UserControl
{
    private const int MaxEntries = 5000;
    private const int MaxPendingEntries = 5000;
    private const int PageSize = 300;
    private readonly List<ToolLogEntry> _history = [];
    private readonly ObservableCollection<ToolLogEntry> _entries = [];
    private readonly ConcurrentQueue<ToolLogEntry> _pending = new();
    private readonly DispatcherTimer _refreshTimer;
    private int _pendingCount;
    private int _displayLimit = PageSize;
    private bool _stickToTop = true;
    private bool _loadingPage;

    public LogView()
    {
        InitializeComponent();
        Grid.ItemsSource = _entries;
        App.Log.EntryWritten += OnEntryWritten;
        VerboseBox.IsChecked = App.Log.VerboseEnabled;
        MaxFileSizeBox.Text = (App.Log.MaxFileSizeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        RetentionBox.Text = App.Log.RetainedFileCount.ToString(CultureInfo.InvariantCulture);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
        UpdateStatus();
    }

    private void OnEntryWritten(ToolLogEntry entry)
    {
        _pending.Enqueue(entry);
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingEntries)
        {
            TryDequeuePending(out _);
        }
    }

    private bool FilterEntry(ToolLogEntry entry)
    {
        ToolLogLevel? level = LevelFilterBox.SelectedIndex switch
        {
            1 => ToolLogLevel.Trace,
            2 => ToolLogLevel.Debug,
            3 => ToolLogLevel.Information,
            4 => ToolLogLevel.Warning,
            5 => ToolLogLevel.Error,
            _ => null
        };
        if (level.HasValue && entry.Level != level.Value)
        {
            return false;
        }

        string category = CategoryFilterBox.Text.Trim();
        if (category.Length > 0 && category != "全部"
            && !entry.Category.Contains(category, StringComparison.OrdinalIgnoreCase)) return false;

        string session = SessionFilterBox.Text.Trim();
        if (session.Length > 0 && !entry.SessionId.Contains(session, StringComparison.OrdinalIgnoreCase)) return false;

        string function = FunctionFilterBox.Text.Trim();
        if (function.Length > 0 && function != "全部")
        {
            string hex = function.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? function[2..] : function;
            if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code))
            {
                if (entry.Context?.FunctionCode != code) return false;
            }
            else if (!entry.FunctionCodeText.Contains(function, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (ErrorTypeFilterBox.SelectedIndex > 0)
        {
            string errorType = (ErrorTypeFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
            if (entry.Level != ToolLogLevel.Error || entry.ErrorTypeText != errorType) return false;
        }

        string keyword = KeywordFilterBox.Text.Trim();
        return keyword.Length == 0
            || entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || entry.Details?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || entry.FrameSummary.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<ToolLogEntry> GetFilteredEntries() => _history.Where(FilterEntry);

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        bool changed = false;
        for (int index = 0; index < 250 && TryDequeuePending(out ToolLogEntry? entry); index++)
        {
            _history.Insert(0, entry);
            if (_history.Count > MaxEntries)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            if (FilterEntry(entry))
            {
                _entries.Insert(0, entry);
                while (_entries.Count > _displayLimit)
                {
                    _entries.RemoveAt(_entries.Count - 1);
                }
            }

            changed = true;
        }

        if (!changed)
        {
            return;
        }

        UpdateStatus();
        if (IsVisible && _stickToTop && _entries.Count > 0)
        {
            Grid.ScrollIntoView(_entries[0]);
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_history is null || SessionFilterBox is null || FunctionFilterBox is null)
        {
            return;
        }

        _displayLimit = PageSize;
        RebuildDisplayedEntries();
    }

    private void RebuildDisplayedEntries()
    {
        _entries.Clear();
        foreach (ToolLogEntry entry in GetFilteredEntries().Take(_displayLimit))
        {
            _entries.Add(entry);
        }

        _stickToTop = true;
        UpdateStatus();
    }

    private void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _stickToTop = e.VerticalOffset <= 0;
        double scrollableHeight = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
        if (_loadingPage || scrollableHeight <= 0 || e.VerticalOffset < scrollableHeight - 2)
        {
            return;
        }

        ToolLogEntry[] older = GetFilteredEntries().Skip(_entries.Count).Take(PageSize).ToArray();
        if (older.Length == 0)
        {
            return;
        }

        _loadingPage = true;
        try
        {
            _displayLimit += PageSize;
            foreach (ToolLogEntry entry in older)
            {
                _entries.Add(entry);
            }

            UpdateStatus();
        }
        finally
        {
            _loadingPage = false;
        }
    }

    private void OnVerboseChanged(object sender, RoutedEventArgs e)
    {
        App.Log.VerboseEnabled = VerboseBox.IsChecked == true;
        UpdateStatus();
    }

    private bool TryDequeuePending([NotNullWhen(true)] out ToolLogEntry? entry)
    {
        if (!_pending.TryDequeue(out entry))
        {
            return false;
        }

        Interlocked.Decrement(ref _pendingCount);
        return true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        while (TryDequeuePending(out _))
        {
        }

        _history.Clear();
        _entries.Clear();
        UpdateStatus();
    }

    private void OnExportSelectedClick(object sender, RoutedEventArgs e)
    {
        ToolLogEntry[] selected = Grid.SelectedItems.Cast<ToolLogEntry>().ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "请先在日志表格中选择要导出的记录。";
            return;
        }

        _ = ExportEntriesAsync(selected);
    }

    private void OnExportAllClick(object sender, RoutedEventArgs e) => _ = ExportEntriesAsync(_history.ToArray());

    private async Task ExportEntriesAsync(IReadOnlyCollection<ToolLogEntry> entries)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出运行日志 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"ModbusTool_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, ToolLogCsv.Serialize(entries), new UTF8Encoding(true));
            StatusText.Text = $"已导出 {entries.Count} 条日志：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "导出日志失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnApplyLogSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxFileSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int sizeMb)
            || sizeMb <= 0
            || !int.TryParse(RetentionBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int retained)
            || retained <= 0)
        {
            MessageBox.Show(Window.GetWindow(this), "日志文件大小和保留数量必须为大于 0 的整数。", "日志设置无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        App.Log.MaxFileSizeBytes = (long)sizeMb * 1024 * 1024;
        App.Log.RetainedFileCount = retained;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int matchingCount = GetFilteredEntries().Count();
        if (StatusText is not null)
        {
            string error = string.IsNullOrEmpty(App.Log.LastError) ? string.Empty : $"；文件错误：{App.Log.LastError}";
            StatusText.Text = $"历史缓存 {_history.Count}/{MaxEntries} 条，匹配 {matchingCount} 条，已加载 {_entries.Count} 条；"
                + $"文件队列丢弃 {App.Log.DroppedCount} 条{error}";
        }

        if (EmptyState is not null)
        {
            EmptyState.Visibility = matchingCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Title = _history.Count == 0 ? "暂无日志" : "没有匹配的日志";
            EmptyState.Description = _history.Count == 0
                ? "操作监听、读写寄存器或发生通信异常时，运行日志会在这里记录。"
                : "请调整级别、会话、功能码、错误类型或关键字筛选条件。";
        }
    }
}
