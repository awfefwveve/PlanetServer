using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Planet.Protocol
{
    public static class Framing
    {
        public const int MaximumBytes = 32768;
        public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
        {
            var header = new byte[4];
            await ReadExactly(stream, header, ct);
            int size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (size < 1 || size > MaximumBytes) throw new InvalidDataException("Invalid frame size");
            var body = new byte[size];
            await ReadExactly(stream, body, ct);
            return body;
        }
        private static async Task ReadExactly(Stream stream, byte[] bytes, CancellationToken ct)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, ct);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
        }
        public static async Task WriteAsync(Stream stream, byte[] body, CancellationToken ct)
        {
            if (body.Length < 1 || body.Length > MaximumBytes) throw new InvalidDataException("Invalid frame size");
            var frame = new byte[body.Length + 4];
            frame[0] = (byte)(body.Length >> 24); frame[1] = (byte)(body.Length >> 16);
            frame[2] = (byte)(body.Length >> 8); frame[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            await stream.WriteAsync(frame, 0, frame.Length, ct);
        }
    }
}
