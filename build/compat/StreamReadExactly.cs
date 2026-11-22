// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// Stream.ReadExactlyAsync 是 .NET 7 新增实例方法；net6 测试项目通过扩展方法补齐相同语义。
internal static class StreamReadExactlyExtensions
{
    public static async Task ReadExactlyAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
