// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusTool.Update;

public static class UpdaterEngine
{
    public static void Apply(
        string sourceDirectory,
        string targetDirectory,
        string backupDirectory,
        Action<string>? log = null)
    {
        string source = ValidateDirectory(sourceDirectory, "源目录", mustExist: true);
        string target = ValidateDirectory(targetDirectory, "目标目录", mustExist: true);
        string backup = ValidateDirectory(backupDirectory, "备份目录", mustExist: false);
        ValidateSeparatePaths(source, target, backup);

        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(static item => item).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("更新源目录为空。");
        }

        Directory.CreateDirectory(backup);
        List<string> applied = [];
        try
        {
            foreach (string sourceFile in files)
            {
                string relativePath = Path.GetRelativePath(source, sourceFile);
                string targetFile = GetTargetPath(target, relativePath);
                string backupFile = Path.GetFullPath(Path.Combine(backup, relativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                if (File.Exists(targetFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(targetFile, backupFile, false);
                }

                string temporaryFile = targetFile + $".update-{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(sourceFile, temporaryFile, false);
                    File.Move(temporaryFile, targetFile, true);
                }
                finally
                {
                    if (File.Exists(temporaryFile))
                    {
                        File.Delete(temporaryFile);
                    }
                }

                applied.Add(relativePath);
                log?.Invoke($"已更新 {relativePath}");
            }
        }
        catch (Exception updateException)
        {
            List<Exception> rollbackErrors = [];
            foreach (string relativePath in applied.AsEnumerable().Reverse())
            {
                try
                {
                    string targetFile = GetTargetPath(target, relativePath);
                    string backupFile = Path.GetFullPath(Path.Combine(backup, relativePath));
                    if (File.Exists(backupFile))
                    {
                        File.Copy(backupFile, targetFile, true);
                    }
                    else if (File.Exists(targetFile))
                    {
                        File.Delete(targetFile);
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add(rollbackException);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException("更新失败，且部分文件回滚失败。", [updateException, .. rollbackErrors]);
            }

            throw;
        }
    }

    private static string ValidateDirectory(string path, string fieldName, bool mustExist)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{fieldName}不能是磁盘根目录。");
        }

        if (mustExist && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{fieldName}不存在：{fullPath}");
        }

        return fullPath;
    }

    private static void ValidateSeparatePaths(string source, string target, string backup)
    {
        if (PathsOverlap(source, target))
        {
            throw new InvalidDataException("更新源目录和程序目标目录不能相同或互相包含。");
        }

        if (PathsOverlap(backup, target) || PathsOverlap(backup, source))
        {
            throw new InvalidDataException("更新备份目录必须位于源目录和程序目录之外。");
        }
    }

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrChild(first, second) || IsSameOrChild(second, first);

    private static bool IsSameOrChild(string path, string parent)
    {
        string prefix = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTargetPath(string target, string relativePath)
    {
        string targetFile = Path.GetFullPath(Path.Combine(target, relativePath));
        string prefix = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!targetFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"更新文件路径越界：{relativePath}");
        }

        return targetFile;
    }
}
