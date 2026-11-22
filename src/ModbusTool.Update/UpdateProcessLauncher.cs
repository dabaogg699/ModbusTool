// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Diagnostics;

namespace ModbusTool.Update;

public static class UpdateProcessLauncher
{
    public static ProcessStartInfo CreateStartInfo(
        PreparedUpdate update,
        string targetDirectory,
        int waitForProcessId,
        string restartExecutable,
        bool silent)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentGuard.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentGuard.ThrowIfNullOrWhiteSpace(restartExecutable);
        if (!File.Exists(update.UpdaterPath))
        {
            throw new FileNotFoundException("更新包中缺少独立更新器。", update.UpdaterPath);
        }

        if (!File.Exists(restartExecutable))
        {
            throw new FileNotFoundException("找不到更新后需要重启的主程序。", restartExecutable);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = update.UpdaterPath,
            WorkingDirectory = update.WorkDirectory,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(update.SourceDirectory);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(Path.GetFullPath(targetDirectory));
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(waitForProcessId.ToString());
        startInfo.ArgumentList.Add("--restart");
        startInfo.ArgumentList.Add(Path.GetFullPath(restartExecutable));
        if (silent)
        {
            startInfo.ArgumentList.Add("--silent");
        }

        return startInfo;
    }
}
