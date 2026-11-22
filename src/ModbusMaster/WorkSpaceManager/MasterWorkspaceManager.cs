// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Text.Json;
using System.Text.Json.Serialization;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;

namespace ModbusMaster;

public sealed class MasterWorkspaceManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _sync = new();
    private MasterWorkspace _current = new();

    public MasterWorkspace Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
        private set
        {
            lock (_sync)
            {
                _current = value;
            }
        }
    }

    public string? FilePath { get; private set; }

    public LegacyMasterConfiguration? ImportedLegacyConfiguration { get; private set; }

    public IReadOnlyList<MasterWorkspace> ImportedWorkspaces => ImportedLegacyConfiguration?.Workspaces ?? [];

    public bool IsDirty { get; private set; } = true;

    public event EventHandler? WorkspaceChanged;

    public MasterWorkspaceManager()
    {
    }

    /// <summary>从 JSON 流反序列化工作区（供内置默认模板等只读资源使用），不校验完整性时按 Validate 执行。</summary>
    public static async ValueTask<MasterWorkspace?> DeserializeWorkspaceAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        MasterWorkspace workspace = await JsonSerializer.DeserializeAsync<MasterWorkspace>(
            stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The workspace file is empty.");
        Validate(workspace);
        return workspace;
    }

    public void New(string name = "新建工作区")
    {
        Current = new MasterWorkspace { Name = name };
        FilePath = null;
        ImportedLegacyConfiguration = null;
        IsDirty = true;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkDirty()
    {
        IsDirty = true;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = File.OpenRead(filePath);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (LegacyMasterConfigMigrator.IsLegacy(document.RootElement))
        {
            LegacyMasterConfiguration legacy = LegacyMasterConfigMigrator.Migrate(document.RootElement);
            MasterWorkspace current = legacy.Workspaces[0];
            Validate(current);
            Current = current;
            ImportedLegacyConfiguration = legacy;
            FilePath = null;
            IsDirty = true;
        }
        else
        {
            MasterWorkspace workspace = document.RootElement.Deserialize<MasterWorkspace>(SerializerOptions)
                ?? throw new InvalidDataException("The workspace file is empty.");
            Validate(workspace);
            Current = workspace;
            ImportedLegacyConfiguration = null;
            FilePath = Path.GetFullPath(filePath);
            IsDirty = false;
        }

        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ActivateImportedWorkspace(MasterWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!ImportedWorkspaces.Contains(workspace))
        {
            throw new ArgumentException("The workspace does not belong to the imported legacy configuration.", nameof(workspace));
        }

        Validate(workspace);
        Current = workspace;
        FilePath = null;
        IsDirty = true;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public MasterWorkspace CloneCurrent()
    {
        string json = JsonSerializer.Serialize(Current, SerializerOptions);
        MasterWorkspace clone = JsonSerializer.Deserialize<MasterWorkspace>(json, SerializerOptions)
            ?? throw new InvalidDataException("无法复制当前工作区。");
        foreach (MasterDevice device in clone.Devices)
        {
            device.Id = Guid.NewGuid();
            foreach (MasterReadBlock block in device.ReadBlocks)
            {
                block.Id = Guid.NewGuid();
                foreach (MasterDataPoint point in block.Points)
                {
                    point.Id = Guid.NewGuid();
                }
            }
        }

        return clone;
    }

    public void LoadModel(MasterWorkspace workspace, bool markDirty = true)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Validate(workspace);
        Current = workspace;
        FilePath = null;
        ImportedLegacyConfiguration = null;
        IsDirty = markDirty;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask SaveAsync(
        string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        string? targetPath = filePath ?? FilePath;
        ArgumentGuard.ThrowIfNullOrWhiteSpace(targetPath);
        Validate(Current);
        string fullPath = Path.GetFullPath(targetPath);
        await WriteWorkspaceAsync(fullPath, cancellationToken).ConfigureAwait(false);
        FilePath = fullPath;
        IsDirty = false;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask WriteSnapshotAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        Validate(Current);
        await WriteWorkspaceAsync(Path.GetFullPath(filePath), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteWorkspaceAsync(string fullPath, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Current,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public IReadOnlyList<PollSchedule> BuildPollSchedules()
    {
        MasterWorkspace workspace = Current;
        Validate(workspace);
        List<PollSchedule> schedules = [];
        foreach (MasterDevice device in workspace.Devices.Where(device => device.Enabled))
        {
            foreach (MasterReadBlock block in device.ReadBlocks)
            {
                schedules.Add(new PollSchedule
                {
                    DeviceId = device.Id,
                    BlockId = block.Id,
                    UnitId = device.UnitId,
                    Request = ModbusRequest.CreateRead(block.Function, block.StartAddress, block.Quantity),
                    Interval = TimeSpan.FromMilliseconds(device.PollIntervalMs),
                    StartDelay = TimeSpan.FromMilliseconds(device.PollOffsetMs),
                    Block = block
                });
            }
        }

        return schedules;
    }

    private static void Validate(MasterWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.SchemaVersion != 1)
        {
            throw new NotSupportedException($"Workspace schema version {workspace.SchemaVersion} is not supported.");
        }

        foreach (MasterPointGroup group in workspace.PointGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                throw new InvalidDataException("Point group name cannot be empty.");
            }
        }

        foreach (MasterDevice device in workspace.Devices)
        {
            if (device.PollIntervalMs < 1)
            {
                throw new InvalidDataException($"Device '{device.Name}' has an invalid polling interval.");
            }

            if (device.PollOffsetMs is < 0 or > 60000)
            {
                throw new InvalidDataException($"Device '{device.Name}' has an invalid polling offset.");
            }

            foreach (MasterReadBlock block in device.ReadBlocks)
            {
                if (block.Function is not (ModbusFunction.ReadCoils
                    or ModbusFunction.ReadDiscreteInputs
                    or ModbusFunction.ReadHoldingRegisters
                    or ModbusFunction.ReadInputRegisters))
                {
                    throw new InvalidDataException($"Read block '{block.Name}' has a non-read function.");
                }

                _ = ModbusRequest.CreateRead(block.Function, block.StartAddress, block.Quantity);
                bool isRegisterBlock = block.Function is ModbusFunction.ReadHoldingRegisters
                    or ModbusFunction.ReadInputRegisters;
                if (!isRegisterBlock && block.Points.Count > 0)
                {
                    throw new InvalidDataException("Numeric points can only be assigned to register read blocks.");
                }

                foreach (MasterDataPoint point in block.Points)
                {
                    int registerCount = RegisterConverter.GetRegisterCount(point.DataType);
                    if (point.AddressOffset + registerCount > block.Quantity)
                    {
                        throw new InvalidDataException($"Point '{point.Name}' exceeds its read block range.");
                    }
                }
            }
        }
    }
}
