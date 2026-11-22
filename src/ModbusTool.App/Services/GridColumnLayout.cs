// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows.Controls;

namespace ModbusTool.App.Services;

/// <summary>
/// 保存与恢复 DataGrid 手动拖动后的列宽（仅记录像素宽度）。
/// </summary>
public static class GridColumnLayout
{
    public static void Capture(DataGrid grid, Dictionary<string, Dictionary<string, double>> store)
    {
        if (string.IsNullOrWhiteSpace(grid.Name) || grid.Columns.Count == 0)
        {
            return;
        }

        Dictionary<string, double> widths = [];
        for (int index = 0; index < grid.Columns.Count; index++)
        {
            DataGridColumn column = grid.Columns[index];
            if (column.Width.UnitType != DataGridLengthUnitType.Pixel || column.Width.DisplayValue < 24)
            {
                continue;
            }

            widths[KeyOf(column, index)] = Math.Round(column.Width.DisplayValue, 1);
        }

        if (widths.Count > 0)
        {
            store[grid.Name] = widths;
        }
    }

    public static void Apply(DataGrid grid, IReadOnlyDictionary<string, Dictionary<string, double>> store)
    {
        if (string.IsNullOrWhiteSpace(grid.Name)
            || !store.TryGetValue(grid.Name, out Dictionary<string, double>? widths)
            || widths.Count == 0)
        {
            return;
        }

        for (int index = 0; index < grid.Columns.Count; index++)
        {
            DataGridColumn column = grid.Columns[index];
            if (widths.TryGetValue(KeyOf(column, index), out double width) && width >= 24)
            {
                column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
            }
        }
    }

    private static string KeyOf(DataGridColumn column, int index) =>
        column.Header is string header && !string.IsNullOrWhiteSpace(header) ? header : $"#{index}";
}
