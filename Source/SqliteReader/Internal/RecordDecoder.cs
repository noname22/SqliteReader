using System.Buffers.Binary;
using System.Text;

namespace SqliteReader.Internal;

/// <summary>
/// Decodes SQLite record format payloads into .NET values: null, <see cref="long"/>, <see cref="double"/>,
/// <see cref="string"/> or <see cref="byte"/>[].
/// </summary>
internal static class RecordDecoder
{
    /// <summary>
    /// Decodes fields of <paramref name="record"/> and passes each one to <paramref name="sink"/> together with its
    /// index. Stops after <paramref name="maxFields"/> fields. Returns the number of fields decoded.
    /// </summary>
    public static int Decode<TSink>(ReadOnlySpan<byte> record, Encoding encoding, int maxFields, ref TSink sink)
        where TSink : struct, IFieldSink
    {
        int headerLength = Varint.Read(record, out long headerSize);
        if (headerSize < headerLength || headerSize > record.Length)
        {
            throw new SqliteFormatException($"Invalid record header size {headerSize}.");
        }

        int headerPos = headerLength;
        int bodyPos = (int)headerSize;
        int field = 0;
        while (headerPos < headerSize && field < maxFields)
        {
            headerPos += Varint.Read(record[headerPos..(int)headerSize], out long serialType);
            int size = SerialTypeSize(serialType);
            if (bodyPos + size > record.Length)
            {
                throw new SqliteFormatException("Record field extends beyond the end of the record.");
            }

            sink.Set(field++, DecodeValue(serialType, record.Slice(bodyPos, size), encoding));
            bodyPos += size;
        }

        return field;
    }

    public static object?[] Decode(ReadOnlySpan<byte> record, Encoding encoding)
    {
        var sink = new ListSink { Values = new List<object?>() };
        Decode(record, encoding, int.MaxValue, ref sink);
        return sink.Values.ToArray();
    }

    internal static int SerialTypeSize(long serialType) => serialType switch
    {
        0 or 8 or 9 => 0,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        5 => 6,
        6 or 7 => 8,
        10 or 11 => throw new SqliteFormatException($"Reserved serial type {serialType}."),
        < 0 or > 2L * int.MaxValue + 13 => throw new SqliteFormatException($"Invalid serial type {serialType}."),
        _ => (int)((serialType - 12) / 2),
    };

    internal static object? DecodeValue(long serialType, ReadOnlySpan<byte> data, Encoding encoding) => serialType switch
    {
        0 => null,
        1 => (long)(sbyte)data[0],
        2 => (long)BinaryPrimitives.ReadInt16BigEndian(data),
        3 => (long)(((sbyte)data[0] << 16) | (data[1] << 8) | data[2]),
        4 => (long)BinaryPrimitives.ReadInt32BigEndian(data),
        5 => ((long)BinaryPrimitives.ReadInt16BigEndian(data) << 32) | BinaryPrimitives.ReadUInt32BigEndian(data[2..]),
        6 => BinaryPrimitives.ReadInt64BigEndian(data),
        7 => BinaryPrimitives.ReadDoubleBigEndian(data),
        8 => 0L,
        9 => 1L,
        _ when (serialType & 1) == 0 => data.ToArray(),
        _ => encoding.GetString(data),
    };

    internal interface IFieldSink
    {
        void Set(int index, object? value);
    }

    private struct ListSink : IFieldSink
    {
        public List<object?> Values;

        public void Set(int index, object? value) => Values.Add(value);
    }
}
