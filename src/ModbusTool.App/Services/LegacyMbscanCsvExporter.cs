// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;

namespace ModbusTool.App.Services;

public sealed record LegacyMbscanRow(
    byte UnitId,
    ushort Address,
    ushort Quantity,
    ModbusFunction Function,
    string Name,
    ModbusDataType DataType,
    string ByteOrder,
    double Scale,
    double Offset,
    string Unit,
    string Note);

public static class LegacyMbscanCsvExporter
{
    public const string CompatibilityWarning = "旧版兼容模式：待样本对齐，当前为推断结构";

    private static readonly string[] Header =
    [
        "站号", "地址", "数量", "功能码", "名称", "数据类型", "字节序", "倍率", "偏移量", "单位", "备注"
    ];

    public static async Task ExportAsync(
        string filePath,
        IEnumerable<LegacyMbscanRow> rows,
        CancellationToken ct = default)
    {
        List<IEnumerable<string?>> lines = [Header];
        foreach (LegacyMbscanRow row in rows)
        {
            ct.ThrowIfCancellationRequested();
            lines.Add(
                [
                    row.UnitId.ToString(CultureInfo.InvariantCulture),
                    row.Address.ToString(CultureInfo.InvariantCulture),
                    row.Quantity.ToString(CultureInfo.InvariantCulture),
                    ((byte)row.Function).ToString("X2", CultureInfo.InvariantCulture),
                    row.Name,
                    row.DataType.ToString(),
                    row.ByteOrder,
                    row.Scale.ToString("R", CultureInfo.InvariantCulture),
                    row.Offset.ToString("R", CultureInfo.InvariantCulture),
                    row.Unit,
                    row.Note
                ]);
        }

        await CsvService.WriteAsync(filePath, ["# " + CompatibilityWarning], lines, ct).ConfigureAwait(false);
    }
}
