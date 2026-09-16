namespace SqliteReader;

/// <summary>
/// A row read from an SQLite table. Values are <see langword="null"/>, <see cref="long"/>, <see cref="double"/>,
/// <see cref="string"/> or <see cref="byte"/>[].
/// </summary>
public sealed class SqliteRow
{
    private readonly object?[] _values;

    internal SqliteRow(SqliteTable table, long? rowId, object?[] values)
    {
        Table = table;
        RowId = rowId;
        _values = values;
    }

    /// <summary>The table the row belongs to.</summary>
    public SqliteTable Table { get; }

    /// <summary>The rowid, or <see langword="null"/> for WITHOUT ROWID tables.</summary>
    public long? RowId { get; }

    /// <summary>The number of values, which equals the number of table columns.</summary>
    public int FieldCount => _values.Length;

    /// <summary>The values, in column order.</summary>
    public IReadOnlyList<object?> Values => _values;

    /// <summary>Gets the value of the column at <paramref name="index"/>.</summary>
    public object? this[int index] => _values[index];

    /// <summary>Gets the value of the named column (case-insensitive).</summary>
    /// <exception cref="KeyNotFoundException">The table has no such column.</exception>
    public object? this[string column] => _values[Table.GetColumnIndex(column)];
}
