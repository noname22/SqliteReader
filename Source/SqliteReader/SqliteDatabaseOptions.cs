namespace SqliteReader;

/// <summary>
/// Options for opening a <see cref="SqliteDatabase"/>. The defaults behave like SQLite; the limits help when reading
/// files from untrusted sources.
/// </summary>
public sealed record SqliteDatabaseOptions
{
    /// <summary>The default value of <see cref="MaxRowSize"/>, SQLite's default limit (SQLITE_MAX_LENGTH).</summary>
    public const int DefaultMaxRowSize = 1_000_000_000;

    private readonly long? _maxWalSize;
    private readonly int _maxRowSize = DefaultMaxRowSize;

    /// <summary>The default options.</summary>
    public static SqliteDatabaseOptions Default { get; } = new();

    /// <summary>
    /// Whether <see cref="SqliteDatabase.OpenAsync(string, SqliteDatabaseOptions?, CancellationToken)"/> includes a
    /// write-ahead log (<c>-wal</c> file) next to the database. If false, only the database file is read, so
    /// transactions that haven't been checkpointed yet are missing. Default: true.
    /// </summary>
    /// <remarks>
    /// Anyone who can create files next to the database can change what is read by placing a <c>-wal</c> file there.
    /// Ignored by <see cref="SqliteDatabase.OpenStreamAsync"/>, which only reads the streams it is given.
    /// </remarks>
    public bool UseWalFile { get; init; } = true;

    /// <summary>
    /// Whether <see cref="SqliteDatabase.OpenAsync(string, SqliteDatabaseOptions?, CancellationToken)"/> refuses to
    /// open a database with a hot rollback journal (<c>-journal</c> file) next to it, whose main file may be
    /// inconsistent. If false, the journal is ignored. Default: true.
    /// </summary>
    /// <remarks>
    /// Anyone who can create files next to the database can prevent it from being opened by placing a journal there.
    /// Ignored by <see cref="SqliteDatabase.OpenStreamAsync"/>, which can't detect journals.
    /// </remarks>
    public bool CheckHotJournal { get; init; } = true;

    /// <summary>
    /// The largest write-ahead log, in bytes, that will be read, or null for no limit. The whole log is read and
    /// indexed when the database is opened, so this bounds the time and memory that takes. Opening a database with a
    /// larger log throws <see cref="NotSupportedException"/>. Default: null.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? MaxWalSize
    {
        get => _maxWalSize;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must not be negative.");
            }

            _maxWalSize = value;
        }
    }

    /// <summary>
    /// The largest row, in bytes as stored in the file, that will be read. This also limits the size of any single
    /// string or blob, and the memory needed to read a row. Reading a larger row throws
    /// <see cref="NotSupportedException"/>. Default: <see cref="DefaultMaxRowSize"/>, like SQLite.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxRowSize
    {
        get => _maxRowSize;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be positive.");
            }

            _maxRowSize = value;
        }
    }
}
