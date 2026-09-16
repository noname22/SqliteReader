using SqliteReader.Internal;

namespace SqliteReader.Tests;

public class CreateTableParserTests
{
    [Test]
    public void Parse_ColumnNamesAndTypes()
    {
        var table = CreateTableParser.Parse(
            "CREATE TABLE IF NOT EXISTS main.\"t\" (a, \"b c\" VARCHAR(10, 2) NOT NULL, [d] UNSIGNED BIG INT, `e` TEXT, 'f' -- x\n, g /* , */ REAL)");

        Assert.Multiple(() =>
        {
            Assert.That(table.Columns.Select(c => c.Name), Is.EqualTo(new[] { "a", "b c", "d", "e", "f", "g" }));
            Assert.That(table.Columns.Select(c => c.TypeName), Is.EqualTo(new[] { "", "VARCHAR (10,2)", "UNSIGNED BIG INT", "TEXT", "", "REAL" }));
            Assert.That(table.Columns.Select(c => c.Affinity), Is.EqualTo(new[]
            {
                ColumnAffinity.Blob, ColumnAffinity.Text, ColumnAffinity.Integer, ColumnAffinity.Text, ColumnAffinity.Blob, ColumnAffinity.Real,
            }));
            Assert.That(table.WithoutRowId, Is.False);
            Assert.That(table.PrimaryKey, Is.Empty);
        });
    }

    [Test]
    public void Parse_TableConstraintsAreNotColumns()
    {
        var table = CreateTableParser.Parse(
            "CREATE TABLE t(a INTEGER, b TEXT CHECK (length(b) > (1)), CONSTRAINT pk PRIMARY KEY (a DESC, b COLLATE nocase), " +
            "UNIQUE (b), CHECK (a > 0), FOREIGN KEY (a) REFERENCES other(x) ON DELETE CASCADE) WITHOUT ROWID, STRICT");

        Assert.Multiple(() =>
        {
            Assert.That(table.Columns.Select(c => c.Name), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(table.PrimaryKey, Is.EqualTo(new[] { new PrimaryKeyColumn("a", null), new PrimaryKeyColumn("b", "nocase") }));
            Assert.That(table.PrimaryKeyIsColumnConstraint, Is.False);
            Assert.That(table.WithoutRowId, Is.True);
            Assert.That(table.IsStrict, Is.True);
        });
    }

    [TestCase("CREATE TABLE t(id INTEGER PRIMARY KEY, v)", 0)]
    [TestCase("CREATE TABLE t(v, id integer primary key asc)", 1)]
    [TestCase("CREATE TABLE t(v, id \"INTEGER\" CONSTRAINT c PRIMARY KEY)", 1)]
    [TestCase("CREATE TABLE t(v, id INTEGER, PRIMARY KEY(id DESC))", 1)]
    [TestCase("CREATE TABLE t(id INTEGER PRIMARY KEY DESC, v)", -1)]
    [TestCase("CREATE TABLE t(id INT PRIMARY KEY, v)", -1)]
    [TestCase("CREATE TABLE t(id INTEGER(8) PRIMARY KEY, v)", -1)]
    [TestCase("CREATE TABLE t(id UNSIGNED INTEGER PRIMARY KEY, v)", -1)]
    [TestCase("CREATE TABLE t(id INTEGER, v, PRIMARY KEY(id, v))", -1)]
    [TestCase("CREATE TABLE t(id INTEGER PRIMARY KEY, v) WITHOUT ROWID", -1)]
    public void Layout_DetectsRowIdAlias(string sql, int expectedAlias)
    {
        var layout = TableLayout.Create(CreateTableParser.Parse(sql));

        Assert.That(layout.RowIdAlias, Is.EqualTo(expectedAlias));
    }

    [Test]
    public void Layout_WithoutRowIdStoresPrimaryKeyFirst()
    {
        var layout = TableLayout.Create(CreateTableParser.Parse(
            "CREATE TABLE t(a, b, c COLLATE NOCASE, d AS (a) VIRTUAL, e, PRIMARY KEY (e, c, a, c COLLATE BINARY, c)) WITHOUT ROWID"));

        Assert.That(layout.StoredToColumn, Is.EqualTo(new[] { 4, 2, 0, 2, 1 }));
    }

    [Test]
    public void Layout_RowIdTableSkipsVirtualColumns()
    {
        var layout = TableLayout.Create(CreateTableParser.Parse(
            "CREATE TABLE t(a, b GENERATED ALWAYS AS (a + 1), c AS (a) STORED, d AS (a) VIRTUAL, e)"));

        Assert.That(layout.StoredToColumn, Is.EqualTo(new[] { 0, 2, 4 }));
    }

    [TestCase("x INTEGER DEFAULT 5", 5L)]
    [TestCase("x INTEGER DEFAULT -5", -5L)]
    [TestCase("x INTEGER DEFAULT (-5)", -5L)]
    [TestCase("x INTEGER DEFAULT '12'", 12L)]
    [TestCase("x INTEGER DEFAULT 1.0", 1L)]
    [TestCase("x INTEGER DEFAULT 0x1F", 31L)]
    [TestCase("x INTEGER DEFAULT 1_000", 1000L)]
    [TestCase("x DEFAULT '12'", "12")]
    [TestCase("x TEXT DEFAULT 12", "12")]
    [TestCase("x TEXT DEFAULT 1.5", "1.5")]
    [TestCase("x TEXT DEFAULT 2e0", "2.0")]
    [TestCase("x REAL DEFAULT 2", 2.0)]
    [TestCase("x DEFAULT 2.5e1", 25.0)]
    [TestCase("x DEFAULT NULL", null)]
    [TestCase("x DEFAULT TRUE", 1L)]
    [TestCase("x DEFAULT false", 0L)]
    [TestCase("x DEFAULT CURRENT_TIMESTAMP", null)]
    [TestCase("x DEFAULT (abs(-1))", null)]
    [TestCase("x DEFAULT (1 + 2)", null)]
    [TestCase("x DEFAULT bareword", "bareword")]
    [TestCase("x DEFAULT 'it''s'", "it's")]
    [TestCase("x NOT NULL DEFAULT -9223372036854775808", long.MinValue)]
    [TestCase("x", null)]
    [TestCase("x INTEGER DEFAULT 0x1FFFFFFFFFFFFFFFFF", null)]
    [TestCase("x INTEGER DEFAULT (0x1FFFFFFFFFFFFFFFFF)", null)]
    [TestCase("x DEFAULT 1.2.3", null)]
    [TestCase("x DEFAULT X'ABC'", null)]
    [TestCase("x DEFAULT X'GG'", null)]
    public void Parse_DefaultValues(string column, object? expected)
    {
        var table = CreateTableParser.Parse($"CREATE TABLE t({column})");

        Assert.That(table.Columns[0].DefaultValue, Is.EqualTo(expected));
        if (expected is not null)
        {
            Assert.That(table.Columns[0].DefaultValue, Is.TypeOf(expected.GetType()));
        }
    }

    [Test]
    public void Parse_BlobDefault()
    {
        var table = CreateTableParser.Parse("CREATE TABLE t(x BLOB DEFAULT X'CAfe')");

        Assert.That(table.Columns[0].DefaultValue, Is.EqualTo(new byte[] { 0xCA, 0xFE }));
    }

    [Test]
    public void Parse_ThrowsWithoutColumnList()
    {
        Assert.Throws<SqliteFormatException>(() => CreateTableParser.Parse("CREATE TABLE t"));
    }
}
