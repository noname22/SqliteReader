namespace SqliteReader.Internal;

/// <summary>
/// SQLite variable-length big-endian integers (1-9 bytes).
/// </summary>
internal static class Varint
{
    /// <summary>
    /// Reads a varint from the start of <paramref name="source"/> and returns the number of bytes consumed.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> source, out long value)
    {
        ulong result = 0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= source.Length)
            {
                throw new SqliteFormatException("Truncated varint.");
            }

            byte b = source[i];
            result = (result << 7) | (uint)(b & 0x7F);
            if (b < 0x80)
            {
                value = (long)result;
                return i + 1;
            }
        }

        if (source.Length < 9)
        {
            throw new SqliteFormatException("Truncated varint.");
        }

        value = (long)((result << 8) | source[8]);
        return 9;
    }
}
