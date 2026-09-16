namespace SqliteReader.Internal;

/// <summary>
/// A read-only, page-addressed view of a database and its write-ahead log, if any.
/// Reads are positional and thread-safe.
/// </summary>
internal sealed class DatabaseFile : IDisposable
{
    private static ReadOnlySpan<byte> JournalMagic => [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];

    private readonly StreamSource? _source;
    private readonly WalIndex? _wal;
    private readonly Stream[] _ownedStreams;
    private bool _disposed;

    private DatabaseFile(StreamSource? source, WalIndex? wal, DatabaseHeader? header, uint pageCount, Stream[] ownedStreams)
    {
        _source = source;
        _wal = wal;
        Header = header;
        PageCount = pageCount;
        _ownedStreams = ownedStreams;
    }

    /// <summary>The header, or null if the database is empty (a valid, empty database).</summary>
    public DatabaseHeader? Header { get; }

    public uint PageCount { get; }

    public int PageSize => Header!.PageSize;

    /// <summary>
    /// Opens a database from streams. Unless <paramref name="leaveOpen"/> is set, the streams are disposed with the
    /// returned object.
    /// </summary>
    public static async Task<DatabaseFile> OpenAsync(Stream database, Stream? wal, bool leaveOpen,
        CancellationToken cancellationToken)
    {
        var source = new StreamSource(database, nameof(database));
        var walSource = wal is null ? null : new StreamSource(wal, nameof(wal));
        Stream[] owned = leaveOpen ? [] : wal is null ? [database] : [database, wal];

        // Like SQLite, a write-ahead log next to an empty database file is ignored.
        if (source.Length == 0)
        {
            return new DatabaseFile(null, null, null, 0, owned);
        }

        var buffer = new byte[DatabaseHeader.Size];
        await source.ReadExactlyAsync(0, buffer, "database", cancellationToken).ConfigureAwait(false);
        var header = DatabaseHeader.Parse(buffer);

        // Like SQLite, a write-ahead log is used whether or not the header says the database is in WAL mode.
        var walIndex = walSource is null
            ? null
            : await WalIndex.OpenAsync(walSource, cancellationToken).ConfigureAwait(false);
        if (walIndex is null)
        {
            uint pageCount = header.PageCount;
            if (pageCount == 0)
            {
                pageCount = (uint)Math.Min(uint.MaxValue, source.Length / header.PageSize);
            }

            return new DatabaseFile(source, null, header, pageCount, owned);
        }

        if (walIndex.PageSize != header.PageSize)
        {
            throw new SqliteFormatException(
                $"The write-ahead log page size {walIndex.PageSize} differs from the database page size {header.PageSize}.");
        }

        if (walIndex.TryGetFrame(1, out long offset))
        {
            await walIndex.ReadAsync(offset, buffer, cancellationToken).ConfigureAwait(false);
            header = DatabaseHeader.Parse(buffer);
        }

        return new DatabaseFile(source, walIndex, header, walIndex.PageCount, owned);
    }

    /// <summary>
    /// The reader never modifies the database, so it cannot roll back a hot journal. Refuses to open databases
    /// whose main file may be inconsistent.
    /// </summary>
    public static async Task CheckHotJournalAsync(string databasePath, CancellationToken cancellationToken)
    {
        string journalPath = databasePath + "-journal";
        await using var journal = OpenFileIfExists(journalPath);
        if (journal is null || journal.Length < JournalMagic.Length)
        {
            return;
        }

        var magic = new byte[JournalMagic.Length];
        await journal.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (magic.AsSpan().SequenceEqual(JournalMagic))
        {
            throw new NotSupportedException(
                $"The database has a hot rollback journal '{Path.GetFileName(journalPath)}'. Open it with SQLite once to recover it.");
        }
    }

    /// <summary>Opens a file for shared, read-only, random access.</summary>
    public static FileStream OpenFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
        BufferSize = 0,
    });

    /// <summary>Like <see cref="OpenFile"/>, but returns null if the file doesn't exist.</summary>
    public static FileStream? OpenFileIfExists(string path)
    {
        try
        {
            return OpenFile(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads consecutive pages. Pages with a committed frame in the write-ahead log are read from the log;
    /// the others are read from the database.
    /// </summary>
    public async ValueTask ReadPagesAsync(uint firstPage, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = _source!;
        int pageSize = PageSize;
        if (_wal is null)
        {
            await source.ReadExactlyAsync((firstPage - 1L) * pageSize, destination, "database", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        int count = destination.Length / pageSize;
        for (int i = 0; i < count;)
        {
            uint page = firstPage + (uint)i;
            if (_wal.TryGetFrame(page, out long offset))
            {
                await _wal.ReadAsync(offset, destination.Slice(i * pageSize, pageSize), cancellationToken).ConfigureAwait(false);
                i++;
                continue;
            }

            int run = 1;
            while (i + run < count && !_wal.TryGetFrame(page + (uint)run, out _))
            {
                run++;
            }

            await source.ReadExactlyAsync((page - 1L) * pageSize, destination.Slice(i * pageSize, run * pageSize),
                "database", cancellationToken).ConfigureAwait(false);
            i += run;
        }
    }

    public void ValidatePageNumber(uint pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
        {
            throw new SqliteFormatException($"Invalid page number {pageNumber} (page count {PageCount}).");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var stream in _ownedStreams)
        {
            stream.Dispose();
        }
    }
}
