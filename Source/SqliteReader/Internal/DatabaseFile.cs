using Microsoft.Win32.SafeHandles;

namespace SqliteReader.Internal;

/// <summary>
/// A read-only, page-addressed view of a database file. Reads are positional and thread-safe.
/// </summary>
internal sealed class DatabaseFile : IDisposable
{
    private static ReadOnlySpan<byte> JournalMagic => [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];

    private readonly SafeFileHandle _handle;

    private DatabaseFile(SafeFileHandle handle, DatabaseHeader? header, uint pageCount)
    {
        _handle = handle;
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
        try
        {
            long length = RandomAccess.GetLength(handle);
            if (length == 0)
            {
                await CheckSideFilesAsync(path, isWal: true, cancellationToken).ConfigureAwait(false);
                return new DatabaseFile(handle, null, 0);
            }

            var buffer = new byte[DatabaseHeader.Size];
            await ReadExactlyAsync(handle, buffer, 0, cancellationToken).ConfigureAwait(false);
            var header = DatabaseHeader.Parse(buffer);

            await CheckSideFilesAsync(path, header.IsWal, cancellationToken).ConfigureAwait(false);

            uint pageCount = header.PageCount;
            if (pageCount == 0)
            {
                pageCount = (uint)Math.Min(uint.MaxValue, length / header.PageSize);
            }

            return new DatabaseFile(handle, header, pageCount);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public ValueTask ReadPagesAsync(uint firstPage, Memory<byte> destination, CancellationToken cancellationToken)
    {
        return ReadExactlyAsync(_handle, destination, (firstPage - 1L) * PageSize, cancellationToken);
    }

    public void ValidatePageNumber(uint pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
        {
            throw new SqliteFormatException($"Invalid page number {pageNumber} (page count {PageCount}).");
        }
    }

    public void Dispose() => _handle.Dispose();

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
    /// The reader never modifies the database or takes locks, so it cannot roll back a hot journal or
    /// read pages from a write-ahead log. Refuse to open databases whose main file may be stale or inconsistent.
    /// </summary>
    private static async Task CheckSideFilesAsync(string path, bool isWal, CancellationToken cancellationToken)
    {
        if (isWal)
        {
            var wal = new FileInfo(path + "-wal");
            if (wal.Exists && wal.Length > 0)
            {
                throw new NotSupportedException(
                    $"The database has a non-empty write-ahead log '{wal.Name}'. Checkpoint it before reading.");
            }
        }

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
