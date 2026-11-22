// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Runtime.ExceptionServices;
using System.Windows.Input;

namespace ModbusTool.App.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isRunning;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            if (_onError is null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            _onError!.Invoke(exception);
        }
    }

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        IsRunning = true;
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
