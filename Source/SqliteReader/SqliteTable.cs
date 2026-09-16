using System.Text;
using SqliteReader.Internal;

namespace SqliteReader;

/// <summary>
/// A table in an SQLite database.
/// </summary>
public sealed class SqliteTable
{
    private readonly Dictionary<string, int> _columnIndexes;

    internal SqliteTable(SqliteDatabase database, string name, IReadOnlyList<string> columns, bool withoutRowId,
        uint rootPage, TableLayout layout)
    {
        Database = database;
        Name = name;
        Columns = columns;
        WithoutRowId = withoutRowId;
        RootPage = rootPage;
        Layout = layout;
        _columnIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count; i++)
        {
            _columnIndexes.TryAdd(columns[i], i);
        }
    }

    /// <summary>The table name.</summary>
    public string Name { get; }

    /// <summary>The column names, in declaration order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>True for WITHOUT ROWID tables, whose rows have no <see cref="SqliteRow.RowId"/>.</summary>
    public bool WithoutRowId { get; }

    internal SqliteDatabase Database { get; }

    internal uint RootPage { get; }

    internal TableLayout Layout { get; }

    /// <inheritdoc />
    public override string ToString() => Name;

    internal int GetColumnIndex(string column) =>
        _columnIndexes.TryGetValue(column, out int index)
            ? index
            : throw new KeyNotFoundException($"Table '{Name}' has no column named '{column}'.");

    internal SqliteRow DecodeRow(long rowId, ReadOnlySpan<byte> payload, Encoding encoding)
    {
        var layout = Layout;
        var values = new object?[Columns.Count];
        var sink = new MappedSink(values, layout.StoredToColumn);
        int count = RecordDecoder.Decode(payload, encoding, layout.StoredToColumn.Length, ref sink);
        for (int i = count; i < layout.StoredToColumn.Length; i++)
        {
            int column = layout.StoredToColumn[i];
            values[column] = layout.Defaults[column];
        }

        foreach (int column in layout.RealColumns)
        {
            if (values[column] is long integer)
            {
                values[column] = (double)integer;
            }
        }

        if (layout.RowIdAlias >= 0)
        {
            values[layout.RowIdAlias] = rowId;
        }

        return new SqliteRow(this, WithoutRowId ? null : rowId, values);
    }

    private readonly struct MappedSink(object?[] values, int[] map) : RecordDecoder.IFieldSink
    {
        public void Set(int index, object? value) => values[map[index]] = value;
    }
}
