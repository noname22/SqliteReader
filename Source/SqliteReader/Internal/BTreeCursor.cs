using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SqliteReader.Internal;

/// <summary>
/// Decodes a b-tree cell payload into a value. The payload span is only valid during the call.
/// </summary>
internal delegate T PayloadDecoder<out T>(long rowId, ReadOnlySpan<byte> payload);

/// <summary>
/// Iterates all entries of a table or index b-tree in key order, reading pages on demand.
/// </summary>
internal static class BTreeCursor
{
    private const byte IndexInterior = 0x02;
    private const byte TableInterior = 0x05;
    private const byte IndexLeaf = 0x0A;
    private const byte TableLeaf = 0x0D;

    // SQLite itself limits b-tree depth to 20; anything deeper indicates a corrupt (possibly cyclic) tree.
    private const int MaxDepth = 40;

    public static async IAsyncEnumerable<T> ScanAsync<T>(DatabaseFile file, uint rootPage, bool isIndex,
        PayloadDecoder<T> decoder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = new PageReader(file);
        var buffers = new List<byte[]>();
        var stack = new Stack<Frame>();
        byte[]? overflowPage = null;

        stack.Push(await LoadFrameAsync(reader, rootPage, 0, buffers, isIndex, cancellationToken).ConfigureAwait(false));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = stack.Peek();

            if (frame.IsLeaf)
            {
                if (frame.Next >= frame.CellCount)
                {
                    stack.Pop();
                    continue;
                }

                var cell = ParseCell(frame, frame.Next++, file.Header!.UsableSize);
                if (cell.OverflowPage == 0)
                {
                    yield return decoder(cell.RowId, frame.Buffer.AsSpan(cell.LocalOffset, cell.LocalSize));
                }
                else
                {
                    overflowPage ??= new byte[file.PageSize];
                    yield return await DecodeOverflowAsync(reader, frame.Buffer, cell, overflowPage, decoder,
                        cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            // Interior page: step 2i descends into the left child of cell i (or the right-most child when
            // i == CellCount), step 2i+1 emits the key of cell i (index b-trees only).
            int step = frame.Next++;
            int cellIndex = step / 2;
            if (cellIndex > frame.CellCount || (cellIndex == frame.CellCount && step % 2 == 1))
            {
                stack.Pop();
                continue;
            }

            if (step % 2 == 0)
            {
                uint child = cellIndex == frame.CellCount
                    ? frame.RightChild
                    : BinaryPrimitives.ReadUInt32BigEndian(frame.Buffer.AsSpan(frame.CellPointer(cellIndex)));
                if (stack.Count >= MaxDepth)
                {
                    throw new SqliteFormatException($"B-tree rooted at page {rootPage} is too deep; the database is corrupt.");
                }

                stack.Push(await LoadFrameAsync(reader, child, stack.Count, buffers, isIndex, cancellationToken)
                    .ConfigureAwait(false));
            }
            else if (isIndex)
            {
                var cell = ParseCell(frame, cellIndex, file.Header!.UsableSize);
                if (cell.OverflowPage == 0)
                {
                    yield return decoder(0, frame.Buffer.AsSpan(cell.LocalOffset, cell.LocalSize));
                }
                else
                {
                    overflowPage ??= new byte[file.PageSize];
                    yield return await DecodeOverflowAsync(reader, frame.Buffer, cell, overflowPage, decoder,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async ValueTask<Frame> LoadFrameAsync(PageReader reader, uint pageNumber, int depth,
        List<byte[]> buffers, bool isIndex, CancellationToken cancellationToken)
    {
        if (depth == buffers.Count)
        {
            buffers.Add(new byte[reader.File.PageSize]);
        }

        byte[] buffer = buffers[depth];
        await reader.ReadPageAsync(pageNumber, buffer, cancellationToken).ConfigureAwait(false);
        return Frame.Parse(buffer, pageNumber, isIndex);
    }

    private static Cell ParseCell(Frame frame, int index, int usableSize)
    {
        var page = frame.Buffer.AsSpan(0, usableSize);
        int offset = frame.CellPointer(index);
        int pos = offset;
        long rowId = 0;

        if (frame.PageType is IndexInterior)
        {
            pos += 4;
        }

        pos += Varint.Read(page[pos..], out long payloadSize);
        if (frame.PageType is TableLeaf)
        {
            pos += Varint.Read(page[pos..], out rowId);
        }

        if (payloadSize < 0 || payloadSize > int.MaxValue)
        {
            throw new SqliteFormatException($"Invalid payload size {payloadSize} on page {frame.PageNumber}.");
        }

        int maxLocal = frame.PageType is TableLeaf ? usableSize - 35 : (usableSize - 12) * 64 / 255 - 23;
        int minLocal = (usableSize - 12) * 32 / 255 - 23;
        int localSize;
        if (payloadSize <= maxLocal)
        {
            localSize = (int)payloadSize;
        }
        else
        {
            int surplus = (int)(minLocal + (payloadSize - minLocal) % (usableSize - 4));
            localSize = surplus <= maxLocal ? surplus : minLocal;
        }

        bool hasOverflow = localSize < payloadSize;
        if (pos + localSize + (hasOverflow ? 4 : 0) > usableSize)
        {
            throw new SqliteFormatException($"Cell {index} overflows page {frame.PageNumber}.");
        }

        uint overflowPage = hasOverflow ? BinaryPrimitives.ReadUInt32BigEndian(page[(pos + localSize)..]) : 0;
        if (hasOverflow && overflowPage == 0)
        {
            throw new SqliteFormatException($"Missing overflow page for cell {index} on page {frame.PageNumber}.");
        }

        return new Cell(rowId, (int)payloadSize, pos, localSize, overflowPage);
    }

    private static async ValueTask<T> DecodeOverflowAsync<T>(PageReader reader, byte[] page, Cell cell,
        byte[] overflowPage, PayloadDecoder<T> decoder, CancellationToken cancellationToken)
    {
        byte[] payload = ArrayPool<byte>.Shared.Rent(cell.PayloadSize);
        try
        {
            Buffer.BlockCopy(page, cell.LocalOffset, payload, 0, cell.LocalSize);
            int filled = cell.LocalSize;
            int chunkSize = reader.File.Header!.UsableSize - 4;
            uint next = cell.OverflowPage;
            while (filled < cell.PayloadSize)
            {
                if (next == 0)
                {
                    throw new SqliteFormatException("Overflow chain ends prematurely.");
                }

                await reader.ReadPageAsync(next, overflowPage, cancellationToken).ConfigureAwait(false);
                next = BinaryPrimitives.ReadUInt32BigEndian(overflowPage);
                int count = Math.Min(chunkSize, cell.PayloadSize - filled);
                Buffer.BlockCopy(overflowPage, 4, payload, filled, count);
                filled += count;
            }

            return decoder(cell.RowId, payload.AsSpan(0, cell.PayloadSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private readonly record struct Cell(long RowId, int PayloadSize, int LocalOffset, int LocalSize, uint OverflowPage);

    private sealed class Frame
    {
        private int _headerOffset;

        public required byte[] Buffer { get; init; }

        public uint PageNumber { get; init; }

        public byte PageType { get; init; }

        public int CellCount { get; init; }

        public uint RightChild { get; init; }

        public bool IsLeaf => PageType is TableLeaf or IndexLeaf;

        /// <summary>Iteration state: next cell (leaf) or next step (interior).</summary>
        public int Next { get; set; }

        public static Frame Parse(byte[] buffer, uint pageNumber, bool isIndex)
        {
            int headerOffset = pageNumber == 1 ? DatabaseHeader.Size : 0;
            var header = buffer.AsSpan(headerOffset);
            byte type = header[0];
            bool valid = isIndex ? type is IndexInterior or IndexLeaf : type is TableInterior or TableLeaf;
            if (!valid)
            {
                throw new SqliteFormatException(
                    $"Unexpected page type 0x{type:X2} on page {pageNumber} in {(isIndex ? "index" : "table")} b-tree.");
            }

            bool leaf = type is TableLeaf or IndexLeaf;
            int headerSize = leaf ? 8 : 12;
            int cellCount = BinaryPrimitives.ReadUInt16BigEndian(header[3..]);
            if (headerOffset + headerSize + cellCount * 2 > buffer.Length)
            {
                throw new SqliteFormatException($"Invalid cell count {cellCount} on page {pageNumber}.");
            }

            return new Frame
            {
                Buffer = buffer,
                PageNumber = pageNumber,
                PageType = type,
                CellCount = cellCount,
                RightChild = leaf ? 0 : BinaryPrimitives.ReadUInt32BigEndian(header[8..]),
                _headerOffset = headerOffset + headerSize,
            };
        }

        public int CellPointer(int index)
        {
            int pointer = BinaryPrimitives.ReadUInt16BigEndian(Buffer.AsSpan(_headerOffset + index * 2));
            if (pointer < _headerOffset + CellCount * 2 || pointer >= Buffer.Length)
            {
                throw new SqliteFormatException($"Invalid cell pointer {pointer} on page {PageNumber}.");
            }

            return pointer;
        }
    }
}
