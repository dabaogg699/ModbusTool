// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using System.Windows.Controls;
using ModbusTool.App.Controls;
using ModbusTool.App.Services;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Views;

public partial class SlaveDebugView : UserControl
{
    public SlaveDebugView()
    {
        InitializeComponent();
        foreach (Expander expander in new[] { StationsExpander, ListenExpander })
        {
            expander.Expanded += OnConfigurationExpanderChanged;
            expander.Collapsed += OnConfigurationExpanderChanged;
        }

        UpdateConfigurationVisibility();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpperScroll.MaxHeight = Math.Min(430, Math.Max(220, e.NewSize.Height * 0.55));
    }

    private void OnConfigurationExpanderChanged(object sender, RoutedEventArgs e) => UpdateConfigurationVisibility();

    private void UpdateConfigurationVisibility()
    {
        Visibility visibility = StationsExpander.IsExpanded || ListenExpander.IsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpperScroll.Visibility = visibility;
        UpperSplitter.Visibility = visibility;
        Dispatcher.BeginInvoke(new Action(UpdateOverflowHint));
    }

    private void OnUpperScrollChanged(object sender, RoutedEventArgs e) => UpdateOverflowHint();

    private void UpdateOverflowHint()
    {
        UpperOverflowHint.Visibility = UpperScroll.Visibility == Visibility.Visible && UpperScroll.ScrollableHeight > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnSummarySectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section })
        {
            return;
        }

        Expander? selected = section switch
        {
            "Stations" => StationsExpander,
            "Listen" => ListenExpander,
            _ => null
        };
        if (selected is null)
        {
            return;
        }

        bool expand = !selected.IsExpanded;
        StationsExpander.IsExpanded = false;
        ListenExpander.IsExpanded = false;
        selected.IsExpanded = expand;
        UpperScroll.ScrollToTop();
    }

    public void CaptureState(Dictionary<string, bool> sections, Dictionary<string, Dictionary<string, double>> columns)
    {
        sections["Slave.Stations"] = StationsExpander.IsExpanded;
        sections["Slave.Listen"] = ListenExpander.IsExpanded;
        GridColumnLayout.Capture(StationsGrid, columns);
    }

    public void ApplyState(IReadOnlyDictionary<string, bool> sections, IReadOnlyDictionary<string, Dictionary<string, double>> columns)
    {
        if (sections.TryGetValue("Slave.Stations", out bool stations))
        {
            StationsExpander.IsExpanded = stations;
        }

        if (sections.TryGetValue("Slave.Listen", out bool listen))
        {
            ListenExpander.IsExpanded = listen;
        }

        GridColumnLayout.Apply(StationsGrid, columns);
    }

    private void OnRegisterValueEditing(object sender, RegisterValueEditingEventArgs e)
    {
        if (DataContext is SlaveViewModel viewModel)
        {
            viewModel.EditValue(e);
        }
    }
}
