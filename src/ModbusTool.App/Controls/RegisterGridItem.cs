// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.ComponentModel;
using System.Text;
using ModbusTool.App.ViewModels;

namespace ModbusTool.App.Controls;

public enum GridValueState
{
    Unknown,
    Fresh,
    Stale,
    Error,
    WritePending
}

public sealed class RegisterGridItem : ObservableObject
{
    private string _value = string.Empty;
    private string _hexValue = string.Empty;
    private string _binaryValue = string.Empty;
    private string _textValue = string.Empty;
    private string _status = string.Empty;
    private GridValueState _state = GridValueState.Unknown;

    public required int Address { get; init; }

    public string AddressHex => $"0x{Address:X4}";

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string HexValue
    {
        get => _hexValue;
        set => SetProperty(ref _hexValue, value);
    }

    public string BinaryValue
    {
        get => _binaryValue;
        set => SetProperty(ref _binaryValue, value);
    }

    public string TextValue
    {
        get => _textValue;
        set => SetProperty(ref _textValue, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public GridValueState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    /// <summary>
    /// 16 位寄存器的二进制视图，高位在前，每 4 位用空格分隔。
    /// </summary>
    public static string FormatBinary(ushort value)
    {
        StringBuilder builder = new(19);
        for (int bit = 15; bit >= 0; bit--)
        {
            builder.Append((value >> bit) & 1);
            if (bit % 4 == 0 && bit != 0)
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 线圈 / 离散输入的二进制视图，显示单个 0/1。
    /// </summary>
    public static string FormatBinary(bool value) => value ? "1" : "0";
}

public sealed class RegisterValueEditingEventArgs : CancelEventArgs
{
    public RegisterValueEditingEventArgs(RegisterGridItem item, string proposedValue, string? originalValue = null)
    {
        Item = item;
        ProposedValue = proposedValue;
        OriginalValue = originalValue ?? item.Value;
    }

    public RegisterGridItem Item { get; }

    public string ProposedValue { get; }

    public string OriginalValue { get; }
}
