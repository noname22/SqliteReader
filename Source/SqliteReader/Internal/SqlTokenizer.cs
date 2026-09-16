using System.Text;

namespace SqliteReader.Internal;

internal enum SqlTokenKind
{
    /// <summary>An unquoted identifier or keyword.</summary>
    Word,

    /// <summary>A quoted identifier: "x", [x] or `x`.</summary>
    QuotedIdentifier,

    /// <summary>A string literal: 'x'.</summary>
    String,

    /// <summary>A blob literal: x'ABCD'.</summary>
    Blob,

    Number,

    Punctuation,
}

internal readonly record struct SqlToken(SqlTokenKind Kind, string Text)
{
    public bool IsWord(string word) => Kind == SqlTokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    public bool IsPunctuation(char c) => Kind == SqlTokenKind.Punctuation && Text.Length == 1 && Text[0] == c;

    /// <summary>True for tokens that can name something (identifiers, and string literals, which SQLite also accepts).</summary>
    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier or SqlTokenKind.String;
}

/// <summary>
/// A minimal SQL tokenizer, sufficient for the CREATE TABLE statements stored in sqlite_schema.
/// Quoted tokens are returned dequoted.
/// </summary>
internal static class SqlTokenizer
{
    public static List<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && At(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && At(sql, i + 1) == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c == '"' || c == '`')
            {
                tokens.Add(new SqlToken(SqlTokenKind.QuotedIdentifier, ReadQuoted(sql, ref i, c)));
            }
            else if (c == '\'')
            {
                tokens.Add(new SqlToken(SqlTokenKind.String, ReadQuoted(sql, ref i, c)));
            }
            else if (c == '[')
            {
                int end = sql.IndexOf(']', i + 1);
                if (end < 0)
                {
                    end = sql.Length;
                }

                tokens.Add(new SqlToken(SqlTokenKind.QuotedIdentifier, sql[(i + 1)..end]));
                i = Math.Min(end + 1, sql.Length);
            }
            else if ((c == 'x' || c == 'X') && At(sql, i + 1) == '\'')
            {
                i++;
                tokens.Add(new SqlToken(SqlTokenKind.Blob, ReadQuoted(sql, ref i, '\'')));
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(At(sql, i + 1))))
            {
                int start = i;
                if (c == '0' && (At(sql, i + 1) is 'x' or 'X'))
                {
                    i += 2;
                    while (i < sql.Length && (char.IsAsciiHexDigit(sql[i]) || sql[i] == '_'))
                    {
                        i++;
                    }
                }
                else
                {
                    while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] is '.' or '_'))
                    {
                        i++;
                    }

                    if (i < sql.Length && sql[i] is 'e' or 'E')
                    {
                        int save = i++;
                        if (i < sql.Length && sql[i] is '+' or '-')
                        {
                            i++;
                        }

                        if (i < sql.Length && char.IsAsciiDigit(sql[i]))
                        {
                            while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                            {
                                i++;
                            }
                        }
                        else
                        {
                            i = save;
                        }
                    }
                }

                tokens.Add(new SqlToken(SqlTokenKind.Number, sql[start..i]));
            }
            else if (IsIdentifierChar(c))
            {
                int start = i;
                while (i < sql.Length && (IsIdentifierChar(sql[i]) || char.IsAsciiDigit(sql[i]) || sql[i] == '$'))
                {
                    i++;
                }

                tokens.Add(new SqlToken(SqlTokenKind.Word, sql[start..i]));
            }
            else
            {
                tokens.Add(new SqlToken(SqlTokenKind.Punctuation, c.ToString()));
                i++;
            }
        }

        return tokens;
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetter(c) || c == '_' || c > 0x7F;

    private static string ReadQuoted(string sql, ref int i, char quote)
    {
        var sb = new StringBuilder();
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (At(sql, i + 1) == quote)
                {
                    sb.Append(quote);
                    i += 2;
                    continue;
                }

                i++;
                return sb.ToString();
            }

            sb.Append(sql[i++]);
        }

        return sb.ToString();
    }
}
