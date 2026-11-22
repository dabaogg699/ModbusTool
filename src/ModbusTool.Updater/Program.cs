// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Diagnostics;
using System.IO;
using System.Windows;
using ModbusTool.Update;

namespace ModbusTool.Updater;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        UpdaterArguments? options = null;
        string logPath = Path.Combine(
            Path.GetTempPath(),
            "ModbusTool",
            "updates",
            $"updater-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.log");
        try
        {
            if (args.Length == 0)
            {
                MessageBox.Show(
                    "ModbusTool 独立更新器由主程序调用。\n参数：--source <目录> --target <目录> [--wait-pid <PID>] [--restart <exe>] [--silent]",
                    "ModbusTool 更新器",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return 2;
            }

            options = UpdaterArguments.Parse(args);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await LogAsync(logPath, $"更新器启动，PID {Environment.ProcessId}").ConfigureAwait(false);
            await WaitForMainProcessAsync(options.WaitForProcessId, logPath).ConfigureAwait(false);
            string backupDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(options.Source))!,
                "backup");
            UpdaterEngine.Apply(
                options.Source,
                options.Target,
                backupDirectory,
                message => File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"));
            await LogAsync(logPath, "程序文件更新完成，配置、数据和日志目录保持不变").ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.RestartExecutable))
            {
                string restartPath = Path.GetFullPath(options.RestartExecutable);
                if (!File.Exists(restartPath))
                {
                    throw new FileNotFoundException("更新后找不到需要重启的主程序。", restartPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = restartPath,
                    WorkingDirectory = Path.GetDirectoryName(restartPath)!,
                    UseShellExecute = true
                });
                await LogAsync(logPath, $"已重新启动 {restartPath}").ConfigureAwait(false);
            }

            if (!options.Silent)
            {
                MessageBox.Show("ModbusTool 更新完成。", "更新器", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                await LogAsync(logPath, exception.ToString()).ConfigureAwait(false);
            }
            catch
            {
            }

            if (options?.Silent != true)
            {
                MessageBox.Show(
                    $"更新失败：{exception.Message}\n日志：{logPath}",
                    "ModbusTool 更新器",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return 1;
        }
    }

    private static async Task WaitForMainProcessAsync(int processId, string logPath)
    {
        if (processId <= 0)
        {
            return;
        }

        if (processId == Environment.ProcessId)
        {
            throw new InvalidOperationException("更新器不能等待自身进程退出。");
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
            await LogAsync(logPath, $"等待主程序 PID {processId} 退出").ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await LogAsync(logPath, "主程序已退出").ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await LogAsync(logPath, $"主程序 PID {processId} 已不存在").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("等待主程序退出超过 5 分钟，更新已取消。");
        }
    }

    private static Task LogAsync(string path, string message) =>
        File.AppendAllTextAsync(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
}
