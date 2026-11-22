// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusTool.Update.Tests;

public sealed class UpdaterEngineTests
{
    [Fact]
    public void ApplyReplacesProgramFilesAndPreservesConfigurationDataAndLogs()
    {
        using TempDirectory temp = new();
        string source = temp.CreateDirectory("source");
        string target = temp.CreateDirectory("target");
        string backup = Path.Combine(temp.Path, "backup");
        File.WriteAllText(Path.Combine(target, "ModbusTool.exe"), "old");
        File.WriteAllText(Path.Combine(target, "slave-config.json"), "keep-config");
        File.WriteAllText(Path.Combine(target, "data.bin"), "keep-data");
        Directory.CreateDirectory(Path.Combine(target, "logs"));
        File.WriteAllText(Path.Combine(target, "logs", "today.log"), "keep-log");
        File.WriteAllText(Path.Combine(source, "ModbusTool.exe"), "new");
        File.WriteAllText(Path.Combine(source, "ModbusTool.Updater.exe"), "updater");

        UpdaterEngine.Apply(source, target, backup);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "ModbusTool.exe")));
        Assert.Equal("updater", File.ReadAllText(Path.Combine(target, "ModbusTool.Updater.exe")));
        Assert.Equal("keep-config", File.ReadAllText(Path.Combine(target, "slave-config.json")));
        Assert.Equal("keep-data", File.ReadAllText(Path.Combine(target, "data.bin")));
        Assert.Equal("keep-log", File.ReadAllText(Path.Combine(target, "logs", "today.log")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "ModbusTool.exe")));
    }

    [Fact]
    public void ApplyRollsBackAlreadyReplacedFilesAfterFailure()
    {
        using TempDirectory temp = new();
        string source = temp.CreateDirectory("source");
        string target = temp.CreateDirectory("target");
        string backup = Path.Combine(temp.Path, "backup");
        File.WriteAllText(Path.Combine(target, "a.exe"), "old-a");
        File.WriteAllText(Path.Combine(target, "z"), "blocks-child-directory");
        File.WriteAllText(Path.Combine(source, "a.exe"), "new-a");
        Directory.CreateDirectory(Path.Combine(source, "z"));
        File.WriteAllText(Path.Combine(source, "z", "child.exe"), "new-child");

        Assert.ThrowsAny<Exception>(() => UpdaterEngine.Apply(source, target, backup));

        Assert.Equal("old-a", File.ReadAllText(Path.Combine(target, "a.exe")));
        Assert.Equal("blocks-child-directory", File.ReadAllText(Path.Combine(target, "z")));
        Assert.Empty(Directory.GetFiles(target, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ApplyRejectsOverlappingSourceAndTarget()
    {
        using TempDirectory temp = new();
        string source = temp.CreateDirectory("source");
        string target = Path.Combine(source, "target");
        Directory.CreateDirectory(target);

        Assert.Throws<InvalidDataException>(() => UpdaterEngine.Apply(
            source,
            target,
            Path.Combine(temp.Path, "backup")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ModbusTool-UpdateTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
