// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.Common;

namespace ModbusCore.Tests;

public sealed class RollingFileLogServiceTests
{
    [Fact]
    public async Task WritesAllLevelsAndFlushesUtf8Log()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            DateTimeOffset timestamp = DateTimeOffset.Now;
            int eventCount = 0;
            await using (RollingFileLogService service = new(directory))
            {
                service.VerboseEnabled = true;
                service.EntryWritten += _ => eventCount++;
                service.Write(ToolLogLevel.Debug, "主站.报文", "发送", "01 03 00 00 00 01 84 0A", timestamp);
                service.Write(ToolLogLevel.Information, "系统", "程序启动", (string?)null, timestamp);
                service.Write(ToolLogLevel.Warning, "从站.通信", "响应延迟", (string?)null, timestamp);
                service.Write(ToolLogLevel.Error, "从站.通信", "写入失败",
                    new InvalidOperationException("测试异常"), timestamp);
                service.Write(ToolLogLevel.Trace, "主站.通信", "交换完成", (string?)null, timestamp,
                    new ToolLogContext
                    {
                        SessionId = "M-01",
                        UnitId = 1,
                        FunctionCode = 3,
                        RegisterAddress = 10,
                        FrameSummary = "0103000A0001",
                        ElapsedMilliseconds = 1.25
                    });
                await service.FlushAsync();
            }

            string path = Path.Combine(directory, $"ModbusTool-{timestamp:yyyy-MM-dd}.log");
            string content = await File.ReadAllTextAsync(path);
            Assert.Equal(5, eventCount);
            Assert.Contains("Debug\t主站.报文\t发送", content);
            Assert.Contains("01 03 00 00", content);
            Assert.Contains("Information\t系统\t程序启动", content);
            Assert.Contains("Warning\t从站.通信\t响应延迟", content);
            Assert.Contains("Error\t从站.通信\t写入失败", content);
            Assert.Contains("Trace\t主站.通信\t交换完成", content);
            Assert.Contains("Session=M-01", content);
            Assert.Contains("Unit=1", content);
            Assert.Contains("Function=0x03", content);
            Assert.Contains("Address=10", content);
            Assert.Contains("ElapsedMs=1.25", content);
            Assert.Contains("测试异常", content);
            Assert.Equal(0, await Task.Run(() => Directory.GetFiles(directory, "*.tmp").Length));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RollsFileWhenEntryDateChanges()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            DateTimeOffset first = new(2026, 11, 16, 23, 59, 59, TimeSpan.FromHours(8));
            DateTimeOffset second = first.AddMinutes(2);
            await using (RollingFileLogService service = new(directory, "Slave"))
            {
                service.Write(ToolLogLevel.Information, "测试", "第一天", (string?)null, first);
                service.Write(ToolLogLevel.Information, "测试", "第二天", (string?)null, second);
                await service.FlushAsync();
            }

            string firstFile = Path.Combine(directory, "Slave-2026-11-16.log");
            string secondFile = Path.Combine(directory, "Slave-2026-11-17.log");
            Assert.True(File.Exists(firstFile));
            Assert.True(File.Exists(secondFile));
            Assert.Contains("第一天", await File.ReadAllTextAsync(firstFile));
            Assert.Contains("第二天", await File.ReadAllTextAsync(secondFile));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ConcurrentWritersDoNotLoseEntriesWithinCapacity()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            DateTimeOffset timestamp = DateTimeOffset.Now;
            await using (RollingFileLogService service = new(directory, capacity: 1000))
            {
                service.VerboseEnabled = true;
                Parallel.For(0, 500, index =>
                    service.Write(ToolLogLevel.Debug, "并发", $"条目 {index}", (string?)null, timestamp));
                await service.FlushAsync();
                Assert.Equal(0, service.DroppedCount);
            }

            string path = Path.Combine(directory, $"ModbusTool-{timestamp:yyyy-MM-dd}.log");
            string[] lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(500, lines.Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DebugEntriesAreSkippedUntilVerboseEnabled()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            int eventCount = 0;
            await using (RollingFileLogService service = new(directory))
            {
                service.EntryWritten += _ => eventCount++;
                service.Write(ToolLogLevel.Debug, "主站.报文", "默认不记录", (string?)null);
                service.Write(ToolLogLevel.Information, "系统", "信息仍记录", (string?)null);
                await service.FlushAsync();
                Assert.Equal(1, eventCount);

                service.VerboseEnabled = true;
                service.Write(ToolLogLevel.Debug, "主站.报文", "开启后记录", (string?)null);
                await service.FlushAsync();
                Assert.Equal(2, eventCount);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SizeRollingKeepsConfiguredFileCountAndLatestEntry()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using (RollingFileLogService service = new(
                directory,
                maxFileSizeBytes: 512,
                retainedFileCount: 3))
            {
                for (int index = 0; index < 80; index++)
                {
                    service.Write(ToolLogLevel.Trace, "压测", $"条目 {index:D2} {new string('X', 80)}", (string?)null);
                }

                await service.FlushAsync();
                Assert.Equal(3, service.RetainedFileCount);
            }

            string[] files = Directory.GetFiles(directory, "*.log");
            Assert.Equal(3, files.Length);
            Assert.Contains(files, path => File.ReadAllText(path).Contains("条目 79", StringComparison.Ordinal));
            Assert.All(files, path => Assert.True(new FileInfo(path).Length > 0));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CsvContainsStructuredFieldsAndEscapesMultilineText()
    {
        ToolLogEntry entry = new(
            DateTimeOffset.Now,
            ToolLogLevel.Error,
            "主站.写入",
            "写入失败，\"目标值\"\n第二行",
            "System.Exception: stack",
            new ToolLogContext
            {
                SessionId = "M-CSV",
                UnitId = 2,
                FunctionCode = 6,
                RegisterAddress = 400,
                FrameSummary = "020601900001",
                ElapsedMilliseconds = 2.5
            });

        string csv = ToolLogCsv.Serialize([entry]);

        Assert.StartsWith(ToolLogCsv.Header, csv, StringComparison.Ordinal);
        Assert.Contains("\"M-CSV\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"0x06\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"400\"", csv, StringComparison.Ordinal);
        Assert.Contains("写入失败，\"\"目标值\"\"\n第二行", csv, StringComparison.Ordinal);
        Assert.Contains("\"System.Exception: stack\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameContextExtractsTcpRoutingAndRetainsRawSummary()
    {
        byte[] frame = Convert.FromHexString("0001000000060103000A0001");

        ToolLogContext context = ToolLogContext.ForFrame("M-TCP", frame, tcp: true);

        Assert.Equal("M-TCP", context.SessionId);
        Assert.Equal((byte)1, context.UnitId);
        Assert.Equal((byte)3, context.FunctionCode);
        Assert.Equal(10, context.RegisterAddress);
        Assert.Equal(Convert.ToHexString(frame), context.FrameSummary);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"modbus-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
