// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

// ArgumentException.ThrowIfNullOrWhiteSpace 是 .NET 7 新增 API；net6 旧系统版通过该等价实现保持行为一致。
internal static class ArgumentGuard
{
    public static void ThrowIfNullOrWhiteSpace(
        [NotNull] string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("值不能为 null、空字符串或仅包含空白字符。", parameterName);
        }
    }

    // ObjectDisposedException.ThrowIf / ArgumentOutOfRangeException.ThrowIf* 均为 .NET 8 API，以下为 net6 等价实现。
    public static void ThrowIfDisposed(bool condition, object instance)
    {
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
    }

    public static void ThrowIfLessThan<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"值必须大于或等于 {other}。");
        }
    }

    public static void ThrowIfLessThanOrEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"值必须大于 {other}。");
        }
    }

    // Stopwatch.GetElapsedTime 是 .NET 7 新增静态方法，net6 使用等价的频率换算。
    public static TimeSpan GetElapsedTime(long startingTimestamp)
    {
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startingTimestamp;
        return TimeSpan.FromSeconds(elapsed / (double)System.Diagnostics.Stopwatch.Frequency);
    }
}
