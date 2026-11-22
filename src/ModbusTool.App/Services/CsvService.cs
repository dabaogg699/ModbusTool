// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace ModbusTool.App.Services;

public sealed record CsvDataRow(long LineNumber, IReadOnlyDictionary<string, string> Fields);

public static class CsvService
{
    public static async Task WriteAsync(
        string filePath,
        IEnumerable<string> headers,
        IEnumerable<IEnumerable<string?>> rows,
        CancellationToken cancellationToken = default)
    {
        await using StreamWriter writer = new(filePath, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(string.Join(',', headers.Select(Escape))).ConfigureAwait(false);
        foreach (IEnumerable<string?> row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(',', row.Select(Escape))).ConfigureAwait(false);
        }
    }

    public static IReadOnlyList<CsvDataRow> Read(string filePath)
    {
        using TextFieldParser parser = new(filePath, Encoding.UTF8, true);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;
        if (parser.EndOfData)
        {
            throw new InvalidDataException("CSV 文件为空。");
        }

        string[] headers = parser.ReadFields() ?? throw new InvalidDataException("CSV 表头无效。");
        headers[0] = headers[0].TrimStart('\uFEFF');
        List<CsvDataRow> rows = [];
        while (!parser.EndOfData)
        {
            string[] fields = parser.ReadFields() ?? [];
            if (fields.Length == 1 && string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            if (fields.Length != headers.Length)
            {
                throw new InvalidDataException($"CSV 第 {parser.LineNumber} 行字段数与表头不一致。");
            }

            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Length; index++)
            {
                values[headers[index].Trim()] = fields[index];
            }

            rows.Add(new CsvDataRow(parser.LineNumber, values));
        }

        return rows;
    }

    public static string GetRequired(this CsvDataRow row, string name)
    {
        return row.Fields.TryGetValue(name, out string? value)
            ? value
            : throw new InvalidDataException($"CSV 第 {row.LineNumber} 行缺少 {name} 列。");
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
