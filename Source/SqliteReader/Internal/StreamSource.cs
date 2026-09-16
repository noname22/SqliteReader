using Microsoft.Win32.SafeHandles;

namespace SqliteReader.Internal;

/// <summary>
/// Positional, thread-safe reads from a seekable stream. File streams are read directly through their handle, so
/// concurrent reads don't block each other; other streams are serialized with a lock around seek and read.
/// </summary>
internal sealed class StreamSource
{
    private readonly Stream _stream;
    private readonly SafeFileHandle? _handle;
    private readonly SemaphoreSlim? _lock;

    public StreamSource(Stream stream, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(stream, parameterName);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", parameterName);
        }

        _stream = stream;
        if (stream is FileStream fileStream)
        {
            _handle = fileStream.SafeFileHandle;
        }
        else
        {
            _lock = new SemaphoreSlim(1, 1);
        }

        Length = stream.Length;
    }

    /// <summary>The stream length when the source was created.</summary>
    public long Length { get; }

    /// <summary>Reads until <paramref name="destination"/> is full or the end of the stream is reached.</summary>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_handle is not null)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int read = await RandomAccess.ReadAsync(_handle, destination[total..], offset + total, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        await _lock!.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream.Position = offset;
            return await _stream.ReadAtLeastAsync(destination, destination.Length, throwOnEndOfStream: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Fills <paramref name="destination"/>, or throws <see cref="SqliteFormatException"/> at the end of the stream.</summary>
    public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, string description,
        CancellationToken cancellationToken)
    {
        int read = await ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false);
        if (read < destination.Length)
        {
            throw new SqliteFormatException($"Unexpected end of {description} at offset {offset + read}.");
        }
    }
}
