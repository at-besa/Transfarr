using System;
using System.IO;

namespace Transfarr.Node.Core;

public class ProgressStream : Stream
{
    private readonly Stream _base;
    private readonly Action<int> _onRead;

    public ProgressStream(Stream baseStream, Action<int> onRead)
    {
        _base = baseStream;
        _onRead = onRead;
    }

    public override bool CanRead => _base.CanRead;
    public override bool CanSeek => _base.CanSeek;
    public override bool CanWrite => _base.CanWrite;
    public override long Length => _base.Length;
    public override long Position { get => _base.Position; set => _base.Position = value; }
    public override void Flush() => _base.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _base.Read(buffer, offset, count);
        if (read > 0) _onRead(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _base.Seek(offset, origin);
    public override void SetLength(long value) => _base.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _base.Write(buffer, offset, count);
}
