using System.Buffers.Binary;
using System.Text;

namespace SqliteReader.Internal;

/// <summary>
/// The 100-byte header at the start of every SQLite 3 database file.
/// </summary>
internal sealed class DatabaseHeader
{
    public const int Size = 100;

    private static ReadOnlySpan<byte> Magic => "SQLite format 3\0"u8;

    public int PageSize { get; private init; }

    public int UsableSize { get; private init; }

    /// <summary>The page count from the header, or 0 if the header value is not valid.</summary>
    public uint PageCount { get; private init; }

    public Encoding TextEncoding { get; private init; } = Encoding.UTF8;

    public static DatabaseHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < Size || !header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new SqliteFormatException("File is not an SQLite 3 database.");
        }

        int pageSize = BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        if (pageSize == 1)
        {
            pageSize = 65536;
        }

        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
        {
            throw new SqliteFormatException($"Invalid page size {pageSize}.");
        }

        byte readVersion = header[19];
        if (readVersion is not (1 or 2))
        {
            throw new NotSupportedException($"Unsupported file format read version {readVersion}.");
        }

        int reserved = header[20];
        int usableSize = pageSize - reserved;
        if (usableSize < 480)
        {
            throw new SqliteFormatException($"Invalid usable page size {usableSize}.");
        }

        if (header[21] != 64 || header[22] != 32 || header[23] != 32)
        {
            throw new SqliteFormatException("Invalid payload fractions in header.");
        }

        uint changeCounter = BinaryPrimitives.ReadUInt32BigEndian(header[24..]);
        uint pageCount = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        uint versionValidFor = BinaryPrimitives.ReadUInt32BigEndian(header[92..]);
        if (versionValidFor != changeCounter)
        {
            pageCount = 0;
        }

        uint schemaFormat = BinaryPrimitives.ReadUInt32BigEndian(header[44..]);
        if (schemaFormat > 4)
        {
            throw new NotSupportedException($"Unsupported schema format {schemaFormat}.");
        }

        Encoding encoding = BinaryPrimitives.ReadUInt32BigEndian(header[56..]) switch
        {
            0 or 1 => Encoding.UTF8,
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            var e => throw new SqliteFormatException($"Invalid text encoding {e}."),
        };

        return new DatabaseHeader
        {
            PageSize = pageSize,
            UsableSize = usableSize,
            PageCount = pageCount,
            TextEncoding = encoding,
        };
    }
}
