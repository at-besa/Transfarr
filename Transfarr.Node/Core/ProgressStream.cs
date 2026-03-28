using System;
using System.IO;

namespace Transfarr.Node.Core;

public class ProgressStream : Stream
{
    private readonly Stream innerStream;
    private readonly Action<int> onRead;

    public ProgressStream(Stream baseStream, Action<int> onRead)
    {
        this.innerStream = baseStream;
        this.onRead = onRead;
    }

    public override bool CanRead => innerStream.CanRead;
    public override bool CanSeek => innerStream.CanSeek;
    public override bool CanWrite => innerStream.CanWrite;
    public override long Length => innerStream.Length;
    public override long Position { get => innerStream.Position; set => innerStream.Position = value; }
    public override void Flush() => innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = innerStream.Read(buffer, offset, count);
        if (read > 0) onRead(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
    public override void SetLength(long value) => innerStream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => innerStream.Write(buffer, offset, count);
}
