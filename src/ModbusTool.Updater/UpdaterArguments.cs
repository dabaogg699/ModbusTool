// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusTool.Updater;

internal sealed class UpdaterArguments
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required int WaitForProcessId { get; init; }
    public string? RestartExecutable { get; init; }
    public bool Silent { get; init; }

    public static UpdaterArguments Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        bool silent = false;
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--silent", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"参数 {args[index]} 缺少值。");
            }

            values[args[index]] = args[++index];
        }

        return new UpdaterArguments
        {
            Source = GetRequired(values, "--source"),
            Target = GetRequired(values, "--target"),
            WaitForProcessId = values.TryGetValue("--wait-pid", out string? processId)
                ? int.Parse(processId)
                : 0,
            RestartExecutable = values.TryGetValue("--restart", out string? restart) ? restart : null,
            Silent = silent
        };
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"缺少必需参数 {key}。");
    }
}
