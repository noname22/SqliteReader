using System.Globalization;
using System.Text.Json;

namespace SqliteReader.Tests;

/// <summary>
/// Compares every table against the <c>*.expected.jsonl</c> dumps that create-test-databases.sh produced with sqlite3.
/// </summary>
public class ExpectedDataTests
{
    private static readonly string[] Databases =
    [
        "types", "corruptible", "utf16le", "utf16be", "multipage", "pagesize65536", "autovacuum", "schema", "wal-checkpointed", "wal",
        "wal-restarted",
    ];

    [TestCaseSource(nameof(Databases))]
    public async Task AllTablesMatchSqlite(string name)
    {
        var expected = LoadExpected(TestData.Path(name + ".expected.jsonl"));
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path(name + ".db"));

        Assert.That(expected.Keys, Is.SubsetOf(database.Tables.Select(t => t.Name)));

        foreach (var table in database.Tables)
        {
            var expectedRows = expected.GetValueOrDefault(table.Name) ?? [];
            int index = 0;
            await foreach (var row in database.ReadTableAsync(table))
            {
                Assert.That(index, Is.LessThan(expectedRows.Count), $"Too many rows in {table.Name}");
                AssertRow(table, index, expectedRows[index], row);
                index++;
            }

            Assert.That(index, Is.EqualTo(expectedRows.Count), $"Row count of {table.Name}");
        }
    }

    [Test]
    public async Task SchemaDatabase_ListsOnlyUserTables()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("schema.db"));

        Assert.That(database.Tables.Select(t => t.Name), Is.EqualTo(new[]
        {
            "ipk", "ipk_table_constraint", "not_alias_desc", "not_alias_int", "odd \"name\"", "altered", "generated", "wr",
            "wr_column_pk", "wr_generated", "strict_t", "empty_table", "CREATE TABLE fake(x)",
        }));
        Assert.That(database.Tables.Single(t => t.Name == "wr").WithoutRowId, Is.True);
        Assert.That(database.Tables.Single(t => t.Name == "odd \"name\"").Columns,
            Is.EqualTo(new[] { "select", "from", "a,b", "quoted", "x y" }));
    }

    [Test]
    public async Task SchemaDatabase_InternalTablesCanBeReadByName()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("schema.db"));

        var sequence = await database.ReadAllAsync("SQLITE_SEQUENCE");
        var schema = await database.ReadAllAsync("sqlite_master");

        Assert.Multiple(() =>
        {
            Assert.That(sequence.Select(r => (r["name"], r["seq"])), Is.EqualTo(new[] { ((object?)"ipk", (object?)100L) }));
            Assert.That(schema.Select(r => r["type"]).Distinct(), Is.EquivalentTo(new[] { "table", "index", "view", "trigger" }));
            Assert.That(schema[0].Table.Columns, Is.EqualTo(new[] { "type", "name", "tbl_name", "rootpage", "sql" }));
        });
    }

    [Test]
    public async Task Row_AccessByNameIsCaseInsensitive()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("schema.db"));

        var row = (await database.ReadAllAsync("IPK"))[1];

        Assert.Multiple(() =>
        {
            Assert.That(row.RowId, Is.EqualTo(1L));
            Assert.That(row["ID"], Is.EqualTo(1L));
            Assert.That(row["Name"], Is.EqualTo("a"));
            Assert.That(row[1], Is.EqualTo("a"));
            Assert.That(row.FieldCount, Is.EqualTo(2));
            Assert.That(row.Values, Is.EqualTo(new object[] { 1L, "a" }));
            Assert.That(row.Table.Name, Is.EqualTo("ipk"));
            Assert.Throws<KeyNotFoundException>(() => _ = row["missing"]);
        });
    }

    private static void AssertRow(SqliteTable table, int index, JsonElement expected, SqliteRow actual)
    {
        string where = $"{table.Name} row {index}";
        var expectedRowId = expected[0].ValueKind == JsonValueKind.Null ? (long?)null : expected[0].GetInt64();
        Assert.That(actual.RowId, Is.EqualTo(expectedRowId), where + " rowid");
        Assert.That(actual.FieldCount, Is.EqualTo(expected.GetArrayLength() - 1), where + " field count");

        for (int i = 0; i < actual.FieldCount; i++)
        {
            object? expectedValue = ToValue(expected[i + 1]);
            object? actualValue = actual[i];
            string column = $"{where} column {table.Columns[i]}";
            if (expectedValue is null)
            {
                Assert.That(actualValue, Is.Null, column);
                continue;
            }

            Assert.That(actualValue, Is.TypeOf(expectedValue.GetType()), column);
            if (expectedValue is double d)
            {
                Assert.That(BitConverter.DoubleToInt64Bits((double)actualValue!), Is.EqualTo(BitConverter.DoubleToInt64Bits(d)),
                    $"{column}: expected {d:R}, was {actualValue:R}");
            }
            else
            {
                Assert.That(actualValue, Is.EqualTo(expectedValue), column);
            }
        }
    }

    private static object? ToValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = element[1];
        return element[0].GetString() switch
        {
            "null" => null,
            "integer" => value.GetInt64(),
            "real" => ParseReal(value.GetString()!),
            "text" => value.GetString(),
            "blob" => Convert.FromHexString(value.GetString()!),
            var type => throw new InvalidDataException($"Unknown type {type}"),
        };
    }

    private static double ParseReal(string hexBits) =>
        BitConverter.Int64BitsToDouble(long.Parse(hexBits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));

    private static Dictionary<string, List<JsonElement>> LoadExpected(string path)
    {
        var result = new Dictionary<string, List<JsonElement>>();
        foreach (string line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            string table = document.RootElement.GetProperty("t").GetString()!;
            if (!result.TryGetValue(table, out var rows))
            {
                result[table] = rows = [];
            }

            rows.Add(document.RootElement.GetProperty("r").Clone());
        }

        return result;
    }
}
