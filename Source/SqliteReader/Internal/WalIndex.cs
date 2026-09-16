using System.Buffers.Binary;

namespace SqliteReader.Internal;

/// <summary>
/// An index of the committed frames in a write-ahead log. It is built by scanning the log, the same way SQLite
/// recovers its wal-index, so the -shm file is never used.
/// </summary>
internal sealed class WalIndex
{
    public const int HeaderSize = 32;
    public const int FrameHeaderSize = 24;
    public const uint Magic = 0x377F0682;
    public const uint Version = 3007000;

    private const int ScanChunkBytes = 1 << 20;

    private readonly StreamSource _source;
    private readonly Dictionary<uint, long> _frames;

    private WalIndex(StreamSource source, int pageSize, uint pageCount, Dictionary<uint, long> frames)
    {
        _source = source;
        PageSize = pageSize;
        PageCount = pageCount;
        _frames = frames;
    }

    public int PageSize { get; }

    /// <summary>The database size in pages after the last committed transaction.</summary>
    public uint PageCount { get; }

    /// <summary>
    /// Indexes a write-ahead log. Returns null if the log holds no committed transactions or has an invalid header,
    /// in which case SQLite ignores it too.
    /// </summary>
    /// <exception cref="NotSupportedException">The log is larger than <paramref name="maxSize"/>.</exception>
    public static Task<WalIndex?> OpenAsync(StreamSource source, long? maxSize, CancellationToken cancellationToken)
    {
        if (source.Length > maxSize)
        {
            throw new NotSupportedException(
                $"The write-ahead log is {source.Length} bytes, more than the limit of {maxSize} bytes " +
                $"({nameof(SqliteDatabaseOptions)}.{nameof(SqliteDatabaseOptions.MaxWalSize)}).");
        }

        return ScanAsync(source, cancellationToken);
    }

    public bool TryGetFrame(uint pageNumber, out long dataOffset) => _frames.TryGetValue(pageNumber, out dataOffset);

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        _source.ReadExactlyAsync(offset, destination, "write-ahead log", cancellationToken);

    /// <summary>
    /// The WAL checksum: for each 8-byte chunk with 32-bit words a and b, <c>s1 += a + s2; s2 += b + s1</c>.
    /// </summary>
    internal static void Checksum(ReadOnlySpan<byte> data, bool bigEndian, ref uint s1, ref uint s2)
    {
        for (int i = 0; i + 8 <= data.Length; i += 8)
        {
            uint a, b;
            if (bigEndian)
            {
                a = BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
                b = BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]);
            }
            else
            {
                a = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                b = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            }

            s1 += a + s2;
            s2 += b + s1;
        }
    }

    private static async Task<WalIndex?> ScanAsync(StreamSource source, CancellationToken cancellationToken)
    {
        long length = source.Length;
        if (length <= HeaderSize)
        {
            return null;
        }

        var header = new byte[HeaderSize];
        if (await source.ReadAsync(0, header, cancellationToken).ConfigureAwait(false) < HeaderSize)
        {
            return null;
        }

        var state = ParseHeader(header);
        if (state is null)
        {
            return null;
        }

        int pageSize = state.PageSize;
        int frameSize = FrameHeaderSize + pageSize;
        long frameCount = (length - HeaderSize) / frameSize;
        int framesPerChunk = Math.Max(1, ScanChunkBytes / frameSize);
        var buffer = new byte[framesPerChunk * frameSize];
        var committed = new Dictionary<uint, long>();
        var pending = new List<(uint Page, long Offset)>();
        uint pageCount = 0;

        for (long frame = 0; frame < frameCount;)
        {
            int count = (int)Math.Min(framesPerChunk, frameCount - frame);
            long chunkOffset = HeaderSize + frame * frameSize;
            int read = await source.ReadAsync(chunkOffset, buffer.AsMemory(0, count * frameSize), cancellationToken)
                .ConfigureAwait(false);
            count = read / frameSize;

            for (int i = 0; i < count; i++)
            {
                if (!state.TryDecodeFrame(buffer.AsSpan(i * frameSize, frameSize), out uint page, out uint commitSize))
                {
                    return Finish();
                }

                pending.Add((page, chunkOffset + i * frameSize + FrameHeaderSize));
                if (commitSize != 0)
                {
                    foreach (var (p, offset) in pending)
                    {
                        committed[p] = offset;
                    }

                    pending.Clear();
                    pageCount = commitSize;
                }
            }

            if (count == 0)
            {
                break;
            }

            frame += count;
        }

        return Finish();

        WalIndex? Finish() => pageCount == 0 ? null : new WalIndex(source, pageSize, pageCount, committed);
    }

    /// <summary>Returns the scan state, or null if the header is invalid and the log must be ignored.</summary>
    private static ScanState? ParseHeader(ReadOnlySpan<byte> header)
    {
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        uint pageSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if ((magic & 0xFFFFFFFE) != Magic || pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
        {
            return null;
        }

        bool bigEndian = (magic & 1) != 0;
        uint s1 = 0, s2 = 0;
        Checksum(header[..24], bigEndian, ref s1, ref s2);
        if (s1 != BinaryPrimitives.ReadUInt32BigEndian(header[24..]) ||
            s2 != BinaryPrimitives.ReadUInt32BigEndian(header[28..]))
        {
            return null;
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (version != Version)
        {
            throw new NotSupportedException($"Unsupported write-ahead log format version {version}.");
        }

        return new ScanState
        {
            PageSize = (int)pageSize,
            BigEndian = bigEndian,
            Salt1 = BinaryPrimitives.ReadUInt32BigEndian(header[16..]),
            Salt2 = BinaryPrimitives.ReadUInt32BigEndian(header[20..]),
            S1 = s1,
            S2 = s2,
        };
    }

    private sealed class ScanState
    {
        public int PageSize { get; init; }

        public bool BigEndian { get; init; }

        public uint Salt1 { get; init; }

        public uint Salt2 { get; init; }

        public uint S1 { get; set; }

        public uint S2 { get; set; }

        /// <summary>
        /// Validates a frame (salts, page number and the cumulative checksum) and advances the running checksum.
        /// </summary>
        public bool TryDecodeFrame(ReadOnlySpan<byte> frame, out uint page, out uint commitSize)
        {
            page = BinaryPrimitives.ReadUInt32BigEndian(frame);
            commitSize = BinaryPrimitives.ReadUInt32BigEndian(frame[4..]);
            if (BinaryPrimitives.ReadUInt32BigEndian(frame[8..]) != Salt1 ||
                BinaryPrimitives.ReadUInt32BigEndian(frame[12..]) != Salt2 ||
                page == 0)
            {
                return false;
            }

            uint s1 = S1, s2 = S2;
            Checksum(frame[..8], BigEndian, ref s1, ref s2);
            Checksum(frame.Slice(FrameHeaderSize, PageSize), BigEndian, ref s1, ref s2);
            if (s1 != BinaryPrimitives.ReadUInt32BigEndian(frame[16..]) ||
                s2 != BinaryPrimitives.ReadUInt32BigEndian(frame[20..]))
            {
                return false;
            }

            S1 = s1;
            S2 = s2;
            return true;
        }
    }
}
