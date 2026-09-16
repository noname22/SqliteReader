namespace SqliteReader.Internal;

/// <summary>
/// Describes how the fields of an on-disk record map to table columns.
/// </summary>
internal sealed class TableLayout
{
    /// <summary>Record field index to table column index.</summary>
    public required int[] StoredToColumn { get; init; }

    /// <summary>Per table column: the value to use when an older record lacks the field (ALTER TABLE ADD COLUMN).</summary>
    public required object?[] Defaults { get; init; }

    /// <summary>
    /// Columns with REAL affinity. SQLite stores their integral values as integers and converts them back on read.
    /// </summary>
    public int[] RealColumns { get; init; } = [];

    /// <summary>The INTEGER PRIMARY KEY column that aliases the rowid, or -1.</summary>
    public int RowIdAlias { get; init; } = -1;

    public static TableLayout Identity(int columnCount) => new()
    {
        StoredToColumn = Enumerable.Range(0, columnCount).ToArray(),
        Defaults = new object?[columnCount],
    };

    public static TableLayout Create(TableDefinition definition)
    {
        var columns = definition.Columns;
        var defaults = columns.Select(c => c.DefaultValue).ToArray();
        var stored = new List<int>();
        var realColumns = Enumerable.Range(0, columns.Count)
            .Where(i => columns[i].Affinity == ColumnAffinity.Real)
            .ToArray();

        if (!definition.WithoutRowId)
        {
            int alias = -1;
            if (definition.PrimaryKey.Count == 1)
            {
                int index = IndexOf(columns, definition.PrimaryKey[0].Name);
                if (index >= 0 &&
                    columns[index].TypeName.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) &&
                    !(definition.PrimaryKeyIsColumnConstraint && columns[index].IsPrimaryKeyDescending))
                {
                    alias = index;
                }
            }

            for (int i = 0; i < columns.Count; i++)
            {
                if (!columns[i].IsVirtual)
                {
                    stored.Add(i);
                }
            }

            return new TableLayout { StoredToColumn = stored.ToArray(), Defaults = defaults, RowIdAlias = alias, RealColumns = realColumns };
        }

        // WITHOUT ROWID: the record is the primary key index entry. It holds the primary key columns (exact
        // duplicates removed), followed by all remaining non-virtual columns in declaration order.
        var seen = new HashSet<(int, string)>();
        foreach (var key in definition.PrimaryKey)
        {
            int index = IndexOf(columns, key.Name);
            if (index < 0)
            {
                throw new SqliteFormatException($"Unknown primary key column '{key.Name}'.");
            }

            string collation = (key.Collation ?? columns[index].Collation ?? "BINARY").ToUpperInvariant();
            if (seen.Add((index, collation)))
            {
                stored.Add(index);
            }
        }

        if (stored.Count == 0)
        {
            throw new SqliteFormatException("WITHOUT ROWID table has no primary key.");
        }

        for (int i = 0; i < columns.Count; i++)
        {
            if (!columns[i].IsVirtual && !stored.Contains(i))
            {
                stored.Add(i);
            }
        }

        return new TableLayout { StoredToColumn = stored.ToArray(), Defaults = defaults, RealColumns = realColumns };
    }

    private static int IndexOf(List<ColumnDefinition> columns, string name) =>
        columns.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
