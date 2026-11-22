// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace ModbusTool.App.Controls;

public partial class RegisterGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(RegisterGrid),
        new PropertyMetadata(null, OnItemsSourceChanged));
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(RegisterGrid), new PropertyMetadata(false));
    public static readonly DependencyProperty AllowWriteMenuProperty = DependencyProperty.Register(
        nameof(AllowWriteMenu), typeof(bool), typeof(RegisterGrid), new PropertyMetadata(false));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(RegisterGridItem), typeof(RegisterGrid), new PropertyMetadata(null));
    public static readonly DependencyProperty ExportCommandProperty = DependencyProperty.Register(
        nameof(ExportCommand), typeof(ICommand), typeof(RegisterGrid), new PropertyMetadata(null));
    public static readonly DependencyProperty EmptyTitleProperty = DependencyProperty.Register(
        nameof(EmptyTitle), typeof(string), typeof(RegisterGrid), new PropertyMetadata("暂无数据"));
    public static readonly DependencyProperty EmptyDescriptionProperty = DependencyProperty.Register(
        nameof(EmptyDescription), typeof(string), typeof(RegisterGrid), new PropertyMetadata("当前没有可显示的寄存器。"));
    public static readonly DependencyProperty ShowEmptyStateProperty = DependencyProperty.Register(
        nameof(ShowEmptyState), typeof(bool), typeof(RegisterGrid), new PropertyMetadata(true, OnShowEmptyStateChanged));

    private ICollectionView? _view;
    private RegisterGridItem? _editingItem;
    private string? _editingOriginalValue;

    public RegisterGrid()
    {
        InitializeComponent();
    }

    public event EventHandler<RegisterValueEditingEventArgs>? ValueEditing;
    public event EventHandler<RegisterValueEditingEventArgs>? ValueWriteRequested;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool AllowWriteMenu
    {
        get => (bool)GetValue(AllowWriteMenuProperty);
        set => SetValue(AllowWriteMenuProperty, value);
    }

    public RegisterGridItem? SelectedItem
    {
        get => (RegisterGridItem?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ICommand? ExportCommand
    {
        get => (ICommand?)GetValue(ExportCommandProperty);
        set => SetValue(ExportCommandProperty, value);
    }

    public string EmptyTitle
    {
        get => (string)GetValue(EmptyTitleProperty);
        set => SetValue(EmptyTitleProperty, value);
    }

    public string EmptyDescription
    {
        get => (string)GetValue(EmptyDescriptionProperty);
        set => SetValue(EmptyDescriptionProperty, value);
    }

    public bool ShowEmptyState
    {
        get => (bool)GetValue(ShowEmptyStateProperty);
        set => SetValue(ShowEmptyStateProperty, value);
    }

    public void Refresh() => _view?.Refresh();

    public void CancelEdit() => Grid.CancelEdit(DataGridEditingUnit.Cell);

    private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        RegisterGrid control = (RegisterGrid)sender;
        if (control._view is not null)
        {
            control._view.CollectionChanged -= control.OnViewCollectionChanged;
        }

        control._view = args.NewValue is null ? null : CollectionViewSource.GetDefaultView(args.NewValue);
        if (control._view is not null)
        {
            control._view.Filter = control.FilterItem;
            control._view.CollectionChanged += control.OnViewCollectionChanged;
        }

        control.Grid.ItemsSource = control._view;
        control.UpdateCount();
    }

    private void OnViewCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateCount();

    private bool FilterItem(object item)
    {
        if (item is not RegisterGridItem row)
        {
            return false;
        }

        string filter = FilterBox.Text.Trim();
        return filter.Length == 0
            || row.Address.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.AddressHex.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.HexValue.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.BinaryValue.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.TextValue.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateCount();
    }

    private void OnColumnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item)
        {
            return;
        }

        DataGridColumn? column = tag switch
        {
            "Address" => AddressColumn,
            "AddressHex" => AddressHexColumn,
            "Value" => ValueColumn,
            "Hex" => HexColumn,
            "Binary" => BinaryColumn,
            "Text" => TextColumn,
            "Status" => StatusColumn,
            _ => null
        };
        if (column is not null)
        {
            column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void OnShowEmptyStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((RegisterGrid)sender).UpdateCount();

    private void UpdateCount()
    {
        int count = _view?.Cast<object>().Count() ?? 0;
        if (CountText is not null)
        {
            CountText.Text = $"共 {count} 项";
        }

        if (EmptyState is not null)
        {
            EmptyState.Visibility = ShowEmptyState && count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Column == ValueColumn && e.Row.Item is RegisterGridItem item)
        {
            _editingItem = item;
            _editingOriginalValue = item.Value;
        }
    }

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(Grid, source) is DataGridRow row)
        {
            Grid.SelectedItem = row.Item;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = !AllowWriteMenu || IsReadOnly || SelectedItem is null;
    }

    private void OnWriteValueClick(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not null)
        {
            ValueWriteRequested?.Invoke(this, new RegisterValueEditingEventArgs(SelectedItem, SelectedItem.Value));
        }
    }

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Column != ValueColumn || e.Row.Item is not RegisterGridItem item)
        {
            return;
        }

        string originalValue = ReferenceEquals(_editingItem, item) && _editingOriginalValue is not null
            ? _editingOriginalValue
            : item.Value;
        _editingItem = null;
        _editingOriginalValue = null;
        if (e.EditAction == DataGridEditAction.Cancel)
        {
            item.Value = originalValue;
            return;
        }

        if (e.EditingElement is not TextBox editor)
        {
            return;
        }

        RegisterValueEditingEventArgs args = new(item, editor.Text.Trim(), originalValue);
        ValueEditing?.Invoke(this, args);
        if (args.Cancel)
        {
            e.Cancel = true;
            editor.Text = originalValue;
            item.Value = originalValue;
        }
    }
}
