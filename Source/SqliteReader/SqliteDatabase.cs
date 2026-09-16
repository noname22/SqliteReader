using System.Runtime.CompilerServices;
using SqliteReader.Internal;

namespace SqliteReader;

/// <summary>
/// A read-only SQLite 3 database. Rows are streamed from disk on demand.
/// </summary>
/// <remarks>
/// The database file is opened read-only. Committed transactions in a write-ahead log (<c>-wal</c> file) are
/// included; the log is indexed once when the database is opened. No locks are taken, so the database must not be
/// written to or checkpointed while it is being read. Databases with a hot rollback journal are rejected.
/// Instances are thread-safe; several tables may be enumerated concurrently.
/// </remarks>
public sealed class SqliteDatabase : IAsyncDisposable, IDisposable
{
    private static readonly string[] SchemaColumns = ["type", "name", "tbl_name", "rootpage", "sql"];

    private readonly DatabaseFile _file;
    private readonly Dictionary<string, SqliteTable> _tablesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SqliteTable> _tables = [];

    private SqliteDatabase(DatabaseFile file)
    {
        _file = file;
    }

    /// <summary>The user tables, in schema order. Internal <c>sqlite_*</c> tables are not listed.</summary>
    public IReadOnlyList<SqliteTable> Tables => _tables;

    /// <summary>
    /// Opens a database file for reading and loads its schema.
    /// </summary>
    /// <exception cref="SqliteFormatException">The file is not a valid SQLite 3 database.</exception>
    /// <exception cref="NotSupportedException">The database has a hot rollback journal, or a file format version this reader doesn't know.</exception>
    public static async Task<SqliteDatabase> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var file = await DatabaseFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var database = new SqliteDatabase(file);
        try
        {
            await database.LoadSchemaAsync(cancellationToken).ConfigureAwait(false);
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Enumerates all rows of the named table (case-insensitive) in rowid or primary key order.
    /// Internal tables such as <c>sqlite_schema</c> and <c>sqlite_sequence</c> can also be read by name.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No table has that name.</exception>
    public IAsyncEnumerable<SqliteRow> ReadTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        if (!_tablesByName.TryGetValue(tableName, out var table))
        {
            throw new KeyNotFoundException($"No table named '{tableName}'.");
        }

        return ReadTableAsync(table, cancellationToken);
    }

    /// <summary>
    /// Enumerates all rows of <paramref name="table"/> in rowid or primary key order.
    /// </summary>
    public IAsyncEnumerable<SqliteRow> ReadTableAsync(SqliteTable table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Database != this)
        {
            throw new ArgumentException("The table belongs to a different database.", nameof(table));
        }

        return ReadRowsAsync(table, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _file.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<SqliteRow> ReadRowsAsync(SqliteTable table,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_file.Header is null)
        {
            yield break;
        }

        var encoding = _file.Header.TextEncoding;
        var rows = BTreeCursor.ScanAsync(_file, table.RootPage, table.WithoutRowId,
            (rowId, payload) => table.DecodeRow(rowId, payload, encoding), cancellationToken);
        await foreach (var row in rows.ConfigureAwait(false))
        {
            yield return row;
        }
    }

    private async Task LoadSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = new SqliteTable(this, "sqlite_schema", SchemaColumns, false, 1,
            TableLayout.Identity(SchemaColumns.Length));
        _tablesByName["sqlite_schema"] = schema;
        _tablesByName["sqlite_master"] = schema;

        await foreach (var row in ReadRowsAsync(schema, cancellationToken).ConfigureAwait(false))
        {
            if (row[0] is not "table" || row[1] is not string name || row[3] is not long rootPage || rootPage <= 0 ||
                rootPage > uint.MaxValue || row[4] is not string sql)
            {
                continue;
            }

            TableDefinition definition;
            TableLayout layout;
            try
            {
                definition = CreateTableParser.Parse(sql);
                layout = TableLayout.Create(definition);
            }
            catch (Exception e) when (e is SqliteFormatException or FormatException)
            {
                throw new SqliteFormatException($"Cannot parse the definition of table '{name}': {e.Message}", e);
            }

            var columns = definition.Columns.Select(c => c.Name).ToArray();
            var table = new SqliteTable(this, name, columns, definition.WithoutRowId, (uint)rootPage, layout);
            _tablesByName[name] = table;
            if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                _tables.Add(table);
            }
        }
    }
}
