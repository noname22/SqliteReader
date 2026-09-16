namespace SqliteReader;

/// <summary>
/// Thrown when a file is not a valid SQLite 3 database or is corrupt.
/// </summary>
public sealed class SqliteFormatException : Exception
{
    /// <summary>Creates a new <see cref="SqliteFormatException"/>.</summary>
    public SqliteFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates a new <see cref="SqliteFormatException"/>.</summary>
    public SqliteFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
