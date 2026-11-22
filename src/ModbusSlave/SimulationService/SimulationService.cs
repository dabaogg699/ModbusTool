// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusDataModel;

namespace ModbusSlave;

public sealed class SimulationService : IAsyncDisposable
{
    private readonly SlaveStationManager _stations;
    private readonly SlaveService _slaveService;
    private readonly object _sync = new();
    private CancellationTokenSource? _dataCancellation;
    private Task? _dataTask;
    private CancellationTokenSource? _outputCancellation;
    private Task? _outputTask;
    private bool _disposed;

    public SimulationService(SlaveStationManager stations, SlaveService slaveService)
    {
        _stations = stations ?? throw new ArgumentNullException(nameof(stations));
        _slaveService = slaveService ?? throw new ArgumentNullException(nameof(slaveService));
    }

    public event Action<Exception>? Error;

    public bool IsDataRunning
    {
        get
        {
            lock (_sync)
            {
                return _dataTask is { IsCompleted: false };
            }
        }
    }

    public bool IsOutputRunning
    {
        get
        {
            lock (_sync)
            {
                return _outputTask is { IsCompleted: false };
            }
        }
    }

    public async Task StartDataAsync(DataSimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateDataSettings(settings);
        ArgumentGuard.ThrowIfDisposed(_disposed, this);
        await StopDataAsync().ConfigureAwait(false);
        CancellationTokenSource cancellation = new();
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            _dataCancellation = cancellation;
            _dataTask = Task.Run(() => DataLoopAsync(settings, cancellation.Token));
        }
    }

    public async Task StartNoiseAsync(NoiseSimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateNoiseSettings(settings);
        await StartOutputAsync(token => NoiseLoopAsync(settings, token)).ConfigureAwait(false);
    }

    public async Task StopDataAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_sync)
        {
            cancellation = _dataCancellation;
            task = _dataTask;
            _dataCancellation = null;
            _dataTask = null;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }

            cancellation.Dispose();
        }
    }

    public async Task StopOutputAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_sync)
        {
            cancellation = _outputCancellation;
            task = _outputTask;
            _outputCancellation = null;
            _outputTask = null;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }

            cancellation.Dispose();
        }
    }

    public void SimulateDataOnce(DataSimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateDataSettings(settings);
        if (!_stations.TryGetStation(settings.UnitId, out ModbusDataStore? store))
        {
            throw new InvalidOperationException($"Slave station {settings.UnitId} does not exist.");
        }

        int width = RegisterConverter.GetRegisterCount(settings.DataType);
        ushort[] registers = ReadRegisters(store!, settings.Area, settings.StartAddress, settings.RegisterCount);
        for (int offset = 0; offset < registers.Length; offset += width)
        {
            double current = Convert.ToDouble(
                RegisterConverter.Decode(registers.AsSpan(offset, width), settings.DataType, settings.ByteOrder),
                CultureInfo.InvariantCulture);
            double next = settings.Mode == DataSimulationMode.Random
                ? Random.Shared.NextDouble() * (settings.Maximum - settings.Minimum) + settings.Minimum
                : GetNextIncrement(current, settings);
            if (IsInteger(settings.DataType))
            {
                next = settings.Mode == DataSimulationMode.Random ? Math.Floor(next) : Math.Round(next);
            }

            ushort[] encoded = RegisterConverter.Encode(next, settings.DataType, settings.ByteOrder);
            encoded.CopyTo(registers, offset);
        }

        WriteRegisters(store!, settings.Area, settings.StartAddress, registers);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopDataAsync().ConfigureAwait(false);
        await StopOutputAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task StartOutputAsync(Func<CancellationToken, Task> loop)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);
        await StopOutputAsync().ConfigureAwait(false);
        CancellationTokenSource cancellation = new();
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            _outputCancellation = cancellation;
            _outputTask = Task.Run(() => RunOutputLoopAsync(loop, cancellation.Token));
        }
    }

    private async Task DataLoopAsync(DataSimulationSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SimulateDataOnce(settings);
                await Task.Delay(settings.IntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RaiseError(exception);
        }
    }

    private async Task NoiseLoopAsync(NoiseSimulationSettings settings, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int length = Random.Shared.Next(settings.MinimumLength, settings.MaximumLength + 1);
            if (length > 0)
            {
                byte[] data = new byte[length];
                Random.Shared.NextBytes(data);
                await _slaveService.SendRawAsync(
                    data,
                    settings.SendToTcp,
                    settings.SendToSerial,
                    cancellationToken).ConfigureAwait(false);
            }

            int delay = Random.Shared.Next(
                settings.MinimumIntervalMilliseconds,
                settings.MaximumIntervalMilliseconds + 1);
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private async Task RunOutputLoopAsync(
        Func<CancellationToken, Task> loop,
        CancellationToken cancellationToken)
    {
        try
        {
            await loop(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RaiseError(exception);
        }
    }

    private static double GetNextIncrement(double current, DataSimulationSettings settings)
    {
        if (current < settings.Minimum || current > settings.Maximum)
        {
            return settings.Step < 0 ? settings.Maximum : settings.Minimum;
        }

        double next = current + settings.Step;
        if (next > settings.Maximum || next < settings.Minimum)
        {
            return settings.Step < 0 ? settings.Maximum : settings.Minimum;
        }

        return next;
    }

    private static ushort[] ReadRegisters(
        ModbusDataStore store,
        ModbusDataArea area,
        ushort startAddress,
        int count)
    {
        return area == ModbusDataArea.HoldingRegisters
            ? store.ReadHoldingRegisters(startAddress, count)
            : store.ReadInputRegisters(startAddress, count);
    }

    private static void WriteRegisters(
        ModbusDataStore store,
        ModbusDataArea area,
        ushort startAddress,
        ReadOnlySpan<ushort> values)
    {
        if (area == ModbusDataArea.HoldingRegisters)
        {
            store.WriteHoldingRegisters(startAddress, values);
        }
        else
        {
            store.WriteInputRegisters(startAddress, values);
        }
    }

    private static bool IsInteger(ModbusDataType dataType) => dataType is not (
        ModbusDataType.Float or ModbusDataType.Double);

    private static void ValidateDataSettings(DataSimulationSettings settings)
    {
        if (settings.Area is not (ModbusDataArea.HoldingRegisters or ModbusDataArea.InputRegisters))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Data simulation requires a register area.");
        }

        int width = RegisterConverter.GetRegisterCount(settings.DataType);
        if (settings.RegisterCount < width || settings.RegisterCount % width != 0
            || settings.StartAddress + settings.RegisterCount > ModbusDataStore.AreaSize)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The simulation register range is invalid.");
        }

        if (settings.IntervalMilliseconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The simulation interval must be positive.");
        }

        if (!double.IsFinite(settings.Minimum) || !double.IsFinite(settings.Maximum)
            || !double.IsFinite(settings.Step) || settings.Minimum > settings.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The simulation numeric range is invalid.");
        }
    }

    private static void ValidateNoiseSettings(NoiseSimulationSettings settings)
    {
        if (settings.MinimumIntervalMilliseconds < 0
            || settings.MaximumIntervalMilliseconds < settings.MinimumIntervalMilliseconds
            || settings.MinimumLength < 0 || settings.MaximumLength < settings.MinimumLength
            || settings.MaximumLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The noise timing or length range is invalid.");
        }
    }

    private void RaiseError(Exception exception)
    {
        try
        {
            Error?.Invoke(exception);
        }
        catch
        {
        }
    }
}
