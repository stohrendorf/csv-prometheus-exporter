using System;
using System.IO;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Parser
{
    /// <inheritdoc />
    /// <summary>
    /// Simple wrapper around SSH stdout streams, because the library doesn't support <see cref="!:Stream#Read" /> with
    /// offsets other than zero. Additionally exposes the total number of bytes read.
    /// </summary>
    public sealed class SSHStream : Stream
    {
        private readonly Stream _stream;

        public SSHStream([NotNull] Stream stream)
        {
            _stream = stream;
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _stream.Length;

        public override long Position
        {
            get => _stream.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            _stream.Flush();
        }

        public long TotalRead = 0;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (offset == 0)
            {
                var result = _stream.Read(buffer, offset, count);
                TotalRead += result;
                return result;
            }

            var tmp = new byte[count];
            var totalRead = 0;
            while (_stream.CanRead && totalRead < count)
            {
                var current = _stream.Read(tmp, 0, count - totalRead);
                Array.Copy(tmp, 0, buffer, offset + totalRead, current);
                totalRead += current;
            }

            TotalRead += totalRead;
            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new InvalidOperationException();
        }

        public override void SetLength(long value)
        {
            throw new InvalidOperationException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException();
        }
    }
}