using Microsoft.Win32.SafeHandles;

namespace SqliteReader.Internal;

/// <summary>
/// A read-only, page-addressed view of a database file and its write-ahead log, if any.
/// Reads are positional and thread-safe.
/// </summary>
internal sealed class DatabaseFile : IDisposable
{
    private static ReadOnlySpan<byte> JournalMagic => [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];

    private readonly SafeFileHandle _handle;
    private readonly WalIndex? _wal;

    private DatabaseFile(SafeFileHandle handle, WalIndex? wal, DatabaseHeader? header, uint pageCount)
    {
        _handle = handle;
        _wal = wal;
        Header = header;
        PageCount = pageCount;
    }

    /// <summary>The header, or null if the file is empty (a valid, empty database).</summary>
    public DatabaseHeader? Header { get; }

    public uint PageCount { get; }

    public int PageSize => Header!.PageSize;

    public static async Task<DatabaseFile> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        WalIndex? wal = null;
        try
        {
            await CheckHotJournalAsync(path, cancellationToken).ConfigureAwait(false);

            // Like SQLite, a write-ahead log next to an empty database file is ignored.
            long length = RandomAccess.GetLength(handle);
            if (length == 0)
            {
                return new DatabaseFile(handle, null, null, 0);
            }

            var buffer = new byte[DatabaseHeader.Size];
            await ReadExactlyAsync(handle, buffer, 0, cancellationToken).ConfigureAwait(false);
            var header = DatabaseHeader.Parse(buffer);

            // Like SQLite, an existing write-ahead log is used whether or not the header says the database is in
            // WAL mode.
            wal = await WalIndex.OpenAsync(path + "-wal", cancellationToken).ConfigureAwait(false);
            if (wal is null)
            {
                uint pageCount = header.PageCount;
                if (pageCount == 0)
                {
                    pageCount = (uint)Math.Min(uint.MaxValue, length / header.PageSize);
                }

                return new DatabaseFile(handle, null, header, pageCount);
            }

            if (wal.PageSize != header.PageSize)
            {
                throw new SqliteFormatException(
                    $"The write-ahead log page size {wal.PageSize} differs from the database page size {header.PageSize}.");
            }

            if (wal.TryGetFrame(1, out long offset))
            {
                await wal.ReadAsync(offset, buffer, cancellationToken).ConfigureAwait(false);
                header = DatabaseHeader.Parse(buffer);
            }

            return new DatabaseFile(handle, wal, header, wal.PageCount);
        }
        catch
        {
            wal?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads consecutive pages. Pages with a committed frame in the write-ahead log are read from the log;
    /// the others are read from the database file.
    /// </summary>
    public async ValueTask ReadPagesAsync(uint firstPage, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int pageSize = PageSize;
        if (_wal is null)
        {
            await ReadExactlyAsync(_handle, destination, (firstPage - 1L) * pageSize, cancellationToken).ConfigureAwait(false);
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

            await ReadExactlyAsync(_handle, destination.Slice(i * pageSize, run * pageSize), (page - 1L) * pageSize,
                cancellationToken).ConfigureAwait(false);
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
        _wal?.Dispose();
        _handle.Dispose();
    }

    private static async ValueTask ReadExactlyAsync(SafeFileHandle handle, Memory<byte> destination, long offset,
        CancellationToken cancellationToken)
    {
        while (destination.Length > 0)
        {
            int read = await RandomAccess.ReadAsync(handle, destination, offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new SqliteFormatException($"Unexpected end of file at offset {offset}.");
            }

            destination = destination[read..];
            offset += read;
        }
    }

    /// <summary>
    /// The reader never modifies the database, so it cannot roll back a hot journal. Refuse to open databases
    /// whose main file may be inconsistent.
    /// </summary>
    private static async Task CheckHotJournalAsync(string path, CancellationToken cancellationToken)
    {
        var journal = new FileInfo(path + "-journal");
        if (journal.Exists && journal.Length >= JournalMagic.Length)
        {
            using var journalHandle = File.OpenHandle(journal.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            var magic = new byte[JournalMagic.Length];
            await ReadExactlyAsync(journalHandle, magic, 0, cancellationToken).ConfigureAwait(false);
            if (magic.AsSpan().SequenceEqual(JournalMagic))
            {
                throw new NotSupportedException(
                    $"The database has a hot rollback journal '{journal.Name}'. Open it with SQLite once to recover it.");
            }
        }
    }
}
