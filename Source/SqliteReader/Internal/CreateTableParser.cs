using System.Globalization;

namespace SqliteReader.Internal;

internal enum ColumnAffinity
{
    Blob,
    Text,
    Numeric,
    Integer,
    Real,
}

internal sealed record ColumnDefinition(string Name)
{
    public string TypeName { get; set; } = "";

    public string? Collation { get; set; }

    public bool IsPrimaryKey { get; set; }

    public bool IsPrimaryKeyDescending { get; set; }

    /// <summary>VIRTUAL generated columns are not stored in the record.</summary>
    public bool IsVirtual { get; set; }

    /// <summary>The DEFAULT value, already converted to the column's affinity. Null if absent or not a constant.</summary>
    public object? DefaultValue { get; set; }

    /// <summary>True if the column belongs to a STRICT table.</summary>
    public bool IsStrict { get; set; }

    public ColumnAffinity Affinity => IsStrict && TypeName.Equals("ANY", StringComparison.OrdinalIgnoreCase)
        ? ColumnAffinity.Blob
        : CreateTableParser.GetAffinity(TypeName);
}

internal sealed record PrimaryKeyColumn(string Name, string? Collation);

internal sealed class TableDefinition
{
    public List<ColumnDefinition> Columns { get; } = [];

    public List<PrimaryKeyColumn> PrimaryKey { get; } = [];

    /// <summary>True if the primary key was declared as a column constraint (as opposed to a table constraint).</summary>
    public bool PrimaryKeyIsColumnConstraint { get; set; }

    public bool WithoutRowId { get; set; }

    public bool IsStrict { get; set; }
}

/// <summary>
/// Extracts the information needed to decode rows from a CREATE TABLE statement.
/// </summary>
internal static class CreateTableParser
{
    private static readonly HashSet<string> ColumnConstraintKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS",
    };

    private static readonly HashSet<string> TableConstraintKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN",
    };

    public static TableDefinition Parse(string sql)
    {
        var tokens = SqlTokenizer.Tokenize(sql);
        int open = tokens.FindIndex(t => t.IsPunctuation('('));
        if (open < 0)
        {
            throw new SqliteFormatException($"Cannot parse table definition: {sql}");
        }

        var table = new TableDefinition();
        int i = open + 1;
        var item = new List<SqlToken>();
        int depth = 0;
        for (; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 && (token.IsPunctuation(',') || token.IsPunctuation(')')))
            {
                ParseItem(item, table);
                item.Clear();
                if (token.IsPunctuation(')'))
                {
                    break;
                }

                continue;
            }

            if (token.IsPunctuation('('))
            {
                depth++;
            }
            else if (token.IsPunctuation(')'))
            {
                depth--;
            }

            item.Add(token);
        }

        for (i++; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord("WITHOUT") && i + 1 < tokens.Count && tokens[i + 1].IsWord("ROWID"))
            {
                table.WithoutRowId = true;
            }
            else if (tokens[i].IsWord("STRICT"))
            {
                table.IsStrict = true;
            }
        }

        if (table.Columns.Count == 0)
        {
            throw new SqliteFormatException($"Table definition has no columns: {sql}");
        }

        foreach (var column in table.Columns)
        {
            column.IsStrict = table.IsStrict;
            column.DefaultValue = ApplyAffinity(column.DefaultValue, column.Affinity);
        }

        return table;
    }

    internal static ColumnAffinity GetAffinity(string typeName)
    {
        if (typeName.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            return ColumnAffinity.Integer;
        }

        if (typeName.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("CLOB", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("TEXT", StringComparison.OrdinalIgnoreCase))
        {
            return ColumnAffinity.Text;
        }

        if (typeName.Length == 0 || typeName.Contains("BLOB", StringComparison.OrdinalIgnoreCase))
        {
            return ColumnAffinity.Blob;
        }

        if (typeName.Contains("REAL", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("FLOA", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("DOUB", StringComparison.OrdinalIgnoreCase))
        {
            return ColumnAffinity.Real;
        }

        return ColumnAffinity.Numeric;
    }

    internal static object? ApplyAffinity(object? value, ColumnAffinity affinity)
    {
        switch (affinity)
        {
            case ColumnAffinity.Text:
                return value switch
                {
                    long l => l.ToString(CultureInfo.InvariantCulture),
                    double d => FormatReal(d),
                    _ => value,
                };
            case ColumnAffinity.Numeric or ColumnAffinity.Integer:
                if (value is string s && TryParseNumber(s.Trim(), out var number))
                {
                    value = number;
                }

                return value is double real && real == Math.Floor(real) && Math.Abs(real) < 9.2e18 ? (long)real : value;
            case ColumnAffinity.Real:
                if (value is string text && TryParseNumber(text.Trim(), out var parsed))
                {
                    value = parsed;
                }

                return value is long integer ? (double)integer : value;
            default:
                return value;
        }
    }

    private static void ParseItem(List<SqlToken> item, TableDefinition table)
    {
        if (item.Count == 0)
        {
            return;
        }

        if (item[0].Kind == SqlTokenKind.Word && TableConstraintKeywords.Contains(item[0].Text))
        {
            ParseTableConstraint(item, table);
        }
        else
        {
            ParseColumn(item, table);
        }
    }

    private static void ParseTableConstraint(List<SqlToken> item, TableDefinition table)
    {
        for (int j = 0; j + 2 < item.Count; j++)
        {
            if (item[j].IsWord("PRIMARY") && item[j + 1].IsWord("KEY") && item[j + 2].IsPunctuation('('))
            {
                int k = j + 3;
                while (k < item.Count && !item[k].IsPunctuation(')'))
                {
                    string name = item[k++].Text;
                    string? collation = null;
                    int depth = 0;
                    while (k < item.Count && (depth > 0 || !(item[k].IsPunctuation(',') || item[k].IsPunctuation(')'))))
                    {
                        if (item[k].IsPunctuation('('))
                        {
                            depth++;
                        }
                        else if (item[k].IsPunctuation(')'))
                        {
                            depth--;
                        }
                        else if (depth == 0 && item[k].IsWord("COLLATE") && k + 1 < item.Count)
                        {
                            collation = item[++k].Text;
                        }

                        k++;
                    }

                    table.PrimaryKey.Add(new PrimaryKeyColumn(name, collation));
                    if (k < item.Count && item[k].IsPunctuation(','))
                    {
                        k++;
                    }
                }

                return;
            }
        }
    }

    private static void ParseColumn(List<SqlToken> item, TableDefinition table)
    {
        var column = new ColumnDefinition(item[0].Text);
        int j = 1;

        var typeParts = new List<string>();
        while (j < item.Count && !(item[j].Kind == SqlTokenKind.Word && ColumnConstraintKeywords.Contains(item[j].Text)))
        {
            if (item[j].IsPunctuation('('))
            {
                int end = SkipGroup(item, j);
                typeParts.Add("(" + string.Join(",", item.Skip(j + 1).Take(end - j - 2)
                    .Where(t => !t.IsPunctuation(',')).Select(t => t.Text)) + ")");
                j = end;
            }
            else
            {
                typeParts.Add(item[j++].Text);
            }
        }

        column.TypeName = string.Join(" ", typeParts);

        while (j < item.Count)
        {
            var token = item[j];
            if (token.IsWord("CONSTRAINT"))
            {
                j += 2;
            }
            else if (token.IsWord("PRIMARY"))
            {
                column.IsPrimaryKey = true;
                j++;
                if (j < item.Count && item[j].IsWord("KEY"))
                {
                    j++;
                }

                if (j < item.Count && item[j].IsWord("DESC"))
                {
                    column.IsPrimaryKeyDescending = true;
                    j++;
                }
            }
            else if (token.IsWord("DEFAULT"))
            {
                j = ParseDefault(item, j + 1, column);
            }
            else if (token.IsWord("COLLATE") && j + 1 < item.Count)
            {
                column.Collation = item[j + 1].Text;
                j += 2;
            }
            else if (token.IsWord("AS"))
            {
                // GENERATED ALWAYS AS (expr) [VIRTUAL | STORED]; VIRTUAL is the default.
                column.IsVirtual = true;
                j++;
                if (j < item.Count && item[j].IsPunctuation('('))
                {
                    j = SkipGroup(item, j);
                }

                if (j < item.Count && item[j].IsWord("STORED"))
                {
                    column.IsVirtual = false;
                    j++;
                }
            }
            else if (token.IsPunctuation('('))
            {
                j = SkipGroup(item, j);
            }
            else
            {
                j++;
            }
        }

        table.Columns.Add(column);
        if (column.IsPrimaryKey)
        {
            table.PrimaryKey.Add(new PrimaryKeyColumn(column.Name, null));
            table.PrimaryKeyIsColumnConstraint = true;
        }
    }

    /// <summary>Parses a DEFAULT value starting at <paramref name="j"/>; returns the index after it.</summary>
    private static int ParseDefault(List<SqlToken> item, int j, ColumnDefinition column)
    {
        if (j >= item.Count)
        {
            return j;
        }

        if (item[j].IsPunctuation('('))
        {
            int end = SkipGroup(item, j);
            var inner = item.Skip(j + 1).Take(end - j - 2).ToList();
            // Only constant literals are evaluated; any other expression yields null.
            if (!TryParseLiteral(inner, 0, out int consumed) || consumed != inner.Count)
            {
                column.DefaultValue = null;
            }

            return end;
        }

        TryParseLiteral(item, j, out int count);
        return j + Math.Max(count, 1);

        bool TryParseLiteral(List<SqlToken> tokens, int start, out int consumed)
        {
            consumed = 0;
            if (start >= tokens.Count)
            {
                return false;
            }

            var token = tokens[start];
            bool negative = false;
            if (token.IsPunctuation('-') || token.IsPunctuation('+'))
            {
                negative = token.IsPunctuation('-');
                if (start + 1 >= tokens.Count || tokens[start + 1].Kind != SqlTokenKind.Number)
                {
                    return false;
                }

                token = tokens[start + 1];
                consumed = 1;
            }

            consumed++;
            switch (token.Kind)
            {
                case SqlTokenKind.Number:
                    column.DefaultValue = ParseNumberLiteral(token.Text, negative);
                    return column.DefaultValue is not null;
                case SqlTokenKind.String or SqlTokenKind.QuotedIdentifier:
                    column.DefaultValue = token.Text;
                    return true;
                case SqlTokenKind.Blob:
                    column.DefaultValue = TryParseHex(token.Text);
                    return column.DefaultValue is not null;
                case SqlTokenKind.Word when token.IsWord("NULL"):
                    column.DefaultValue = null;
                    return true;
                case SqlTokenKind.Word when token.IsWord("TRUE"):
                    column.DefaultValue = 1L;
                    return true;
                case SqlTokenKind.Word when token.IsWord("FALSE"):
                    column.DefaultValue = 0L;
                    return true;
                case SqlTokenKind.Word when token.Text.StartsWith("CURRENT_", StringComparison.OrdinalIgnoreCase):
                    column.DefaultValue = null;
                    return false;
                case SqlTokenKind.Word:
                    column.DefaultValue = token.Text;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Parses a numeric literal; returns null if it is malformed (SQLite would have rejected it).</summary>
    private static object? ParseNumberLiteral(string text, bool negative)
    {
        text = text.Replace("_", "", StringComparison.Ordinal);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex))
            {
                return null;
            }

            return negative ? unchecked(-(long)hex) : unchecked((long)hex);
        }

        return TryParseNumber((negative ? "-" : "") + text, out var value) ? value : null;
    }

    private static byte[]? TryParseHex(string text)
    {
        return text.Length % 2 == 0 && text.All(char.IsAsciiHexDigit) ? Convert.FromHexString(text) : null;
    }

    private static bool TryParseNumber(string text, out object value)
    {
        bool isReal = text.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
        if (!isReal && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
        {
            value = l;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            value = d;
            return true;
        }

        value = 0L;
        return false;
    }

    private static string FormatReal(double d)
    {
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.AsSpan().IndexOfAny(".EeIN") >= 0 ? s : s + ".0";
    }

    /// <summary>Given the index of an opening parenthesis, returns the index after the matching closing one.</summary>
    private static int SkipGroup(List<SqlToken> tokens, int open)
    {
        int depth = 0;
        for (int k = open; k < tokens.Count; k++)
        {
            if (tokens[k].IsPunctuation('('))
            {
                depth++;
            }
            else if (tokens[k].IsPunctuation(')') && --depth == 0)
            {
                return k + 1;
            }
        }

        return tokens.Count;
    }
}
