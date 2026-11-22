// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using System.Text;

namespace ModbusCore.Common;

public enum ToolLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Trace
}

/// <summary>通信日志上下文；非通信日志保留空值，输出时统一显示为“-”。</summary>
public sealed record ToolLogContext
{
    public string? SessionId { get; init; }
    public byte? UnitId { get; init; }
    public byte? FunctionCode { get; init; }
    public int? RegisterAddress { get; init; }
    public string? FrameSummary { get; init; }
    public double? ElapsedMilliseconds { get; init; }
    public string? ErrorType { get; init; }

    /// <summary>从 TCP 或 RTU 原始帧尽力提取索引字段，解析失败仍保留报文摘要。</summary>
    public static ToolLogContext ForFrame(string sessionId, ReadOnlySpan<byte> data, bool tcp)
    {
        int unitOffset = tcp ? 6 : 0;
        int pduOffset = tcp ? 7 : 1;
        byte? unitId = data.Length > unitOffset ? data[unitOffset] : null;
        byte? functionCode = data.Length > pduOffset ? data[pduOffset] : null;
        int? address = data.Length >= pduOffset + 3
            ? (data[pduOffset + 1] << 8) | data[pduOffset + 2]
            : null;
        int summaryLength = Math.Min(data.Length, 64);
        string summary = Convert.ToHexString(data[..summaryLength]);
        if (summaryLength < data.Length)
        {
            summary += $"…({data.Length}字节)";
        }

        return new ToolLogContext
        {
            SessionId = sessionId,
            UnitId = unitId,
            FunctionCode = functionCode,
            RegisterAddress = address,
            FrameSummary = summary
        };
    }
}

public sealed record ToolLogEntry(
    DateTimeOffset Timestamp,
    ToolLogLevel Level,
    string Category,
    string Message,
    string? Details = null,
    ToolLogContext? Context = null)
{
    public string LevelText => Level switch
    {
        ToolLogLevel.Debug => "调试",
        ToolLogLevel.Information => "信息",
        ToolLogLevel.Warning => "警告",
        ToolLogLevel.Error => "错误",
        ToolLogLevel.Trace => "跟踪",
        _ => Level.ToString()
    };

    public string SessionId => string.IsNullOrWhiteSpace(Context?.SessionId) ? "system" : Context.SessionId;
    public string UnitIdText => Context?.UnitId?.ToString(CultureInfo.InvariantCulture) ?? "-";
    public string FunctionCodeText => Context?.FunctionCode is byte function ? $"0x{function:X2}" : "-";
    public string RegisterAddressText => Context?.RegisterAddress?.ToString(CultureInfo.InvariantCulture) ?? "-";
    public string FrameSummary => string.IsNullOrWhiteSpace(Context?.FrameSummary) ? "-" : Context.FrameSummary;
    public string ElapsedMillisecondsText => Context?.ElapsedMilliseconds?.ToString("F2", CultureInfo.InvariantCulture) ?? "-";
    public string ErrorTypeText => Context?.ErrorType ?? InferErrorType();

    public string ToLogLine()
    {
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff zzz}\t{1}\t{2}\t{3}\tSession={4}\tUnit={5}\tFunction={6}\tAddress={7}\tFrame={8}\tElapsedMs={9}\tErrorType={10}",
            Timestamp,
            Level,
            Category,
            Message,
            SessionId,
            UnitIdText,
            FunctionCodeText,
            RegisterAddressText,
            FrameSummary,
            ElapsedMillisecondsText,
            ErrorTypeText);
        return string.IsNullOrWhiteSpace(Details)
            ? line
            : line + Environment.NewLine + "\t" + Details.Replace(
                Environment.NewLine,
                Environment.NewLine + "\t",
                StringComparison.Ordinal);
    }

    private string InferErrorType()
    {
        if (Level is not (ToolLogLevel.Error or ToolLogLevel.Warning))
        {
            return "-";
        }

        string text = $"{Message} {Details}";
        if (text.Contains("超时", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "超时";
        if (text.Contains("CRC", StringComparison.OrdinalIgnoreCase)) return "CRC";
        if (text.Contains("写入", StringComparison.OrdinalIgnoreCase)) return "写入";
        if (text.Contains("协议", StringComparison.OrdinalIgnoreCase)
            || text.Contains("protocol", StringComparison.OrdinalIgnoreCase)) return "协议";
        if (text.Contains("连接", StringComparison.OrdinalIgnoreCase)
            || text.Contains("通信", StringComparison.OrdinalIgnoreCase)
            || text.Contains("串口", StringComparison.OrdinalIgnoreCase)
            || text.Contains("socket", StringComparison.OrdinalIgnoreCase)) return "通信";
        return "其他";
    }
}

/// <summary>日志 CSV 的统一转义与列顺序，供界面导出和自动化验证共用。</summary>
public static class ToolLogCsv
{
    public const string Header = "Timestamp,Level,Category,SessionId,UnitId,FunctionCode,RegisterAddress,Message,FrameSummary,ElapsedMs,ErrorType,Details";

    public static string Serialize(IEnumerable<ToolLogEntry> entries)
    {
        StringBuilder csv = new(Header + Environment.NewLine);
        foreach (ToolLogEntry entry in entries)
        {
            csv.AppendLine(string.Join(',',
                Escape(entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                Escape(entry.Level.ToString()),
                Escape(entry.Category),
                Escape(entry.SessionId),
                Escape(entry.UnitIdText),
                Escape(entry.FunctionCodeText),
                Escape(entry.RegisterAddressText),
                Escape(entry.Message),
                Escape(entry.FrameSummary),
                Escape(entry.ElapsedMillisecondsText),
                Escape(entry.ErrorTypeText),
                Escape(entry.Details ?? string.Empty)));
        }

        return csv.ToString();
    }

    private static string Escape(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
