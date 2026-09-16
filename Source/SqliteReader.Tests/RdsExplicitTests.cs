using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SqliteReader.Tests;

/// <summary>
/// Tests against the large NSRL RDS database in the repository's TestDatabases directory (not in git). Run with:
/// <c>dotnet test --filter "FullyQualifiedName~RdsExplicitTests"</c>
/// </summary>
[Explicit("Requires the large RDS database in TestDatabases/")]
[Category("Explicit")]
public partial class RdsExplicitTests
{
    private const string Name = "RDS_2026.03.1_modern_minimal";

    private static string Directory => Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "TestDatabases", Name));

    private static string DatabasePath => Path.Combine(Directory, Name + ".db");

    [OneTimeSetUp]
    public void RequireDatabase()
    {
        if (!File.Exists(DatabasePath))
        {
            Assert.Ignore($"{DatabasePath} not found.");
        }
    }

    [Test]
    public async Task Tables_MatchSchemaFile()
    {
        string schema = await File.ReadAllTextAsync(Path.Combine(Directory, Name + ".schema.sql"));
        await using var database = await SqliteDatabase.OpenAsync(DatabasePath);

        var expected = CreateTableRegex().Matches(schema).Select(m => m.Groups[1].Value).ToArray();
        Assert.That(database.Tables.Select(t => t.Name), Is.EquivalentTo(expected));
        Assert.That(database.Tables.Single(t => t.Name == "FILE").Columns,
            Is.EqualTo(new[] { "sha256", "sha1", "md5", "crc32", "file_name", "file_size", "package_id" }));
    }

    [TestCase("VERSION")]
    [TestCase("MFG")]
    [TestCase("OS")]
    [TestCase("PKG")]
    public async Task SmallTables_RowCountsMatchSqlite(string table)
    {
        long expected = SqliteCount(table);
        await using var database = await SqliteDatabase.OpenAsync(DatabasePath);

        long count = 0;
        await foreach (var row in database.ReadTableAsync(table))
        {
            Assert.That(row.FieldCount, Is.EqualTo(row.Table.Columns.Count));
            count++;
        }

        TestContext.Out.WriteLine($"{table}: {count} rows");
        Assert.That(count, Is.EqualTo(expected));
    }

    [Test]
    public async Task FileTable_StreamsWithBoundedMemory()
    {
        const int rowsToRead = 2_000_000;
        await using var database = await SqliteDatabase.OpenAsync(DatabasePath);
        var stopwatch = Stopwatch.StartNew();
        long maxManagedMemory = 0;
        int count = 0;
        long? previousRowId = null;

        await foreach (var row in database.ReadTableAsync("FILE"))
        {
            Assert.That(row["sha256"], Is.TypeOf<string>().And.Length.EqualTo(64));
            Assert.That(row["file_size"], Is.TypeOf<long>());
            Assert.That(row.RowId, Is.GreaterThan(previousRowId ?? long.MinValue));
            previousRowId = row.RowId;

            if (++count % 100_000 == 0)
            {
                maxManagedMemory = Math.Max(maxManagedMemory, GC.GetTotalMemory(forceFullCollection: false));
            }

            if (count == rowsToRead)
            {
                break;
            }
        }

        TestContext.Out.WriteLine(
            $"{count} rows in {stopwatch.Elapsed}, {count / stopwatch.Elapsed.TotalSeconds:F0} rows/s, max managed memory {maxManagedMemory / 1_000_000} MB");
        Assert.That(count, Is.EqualTo(rowsToRead));
        Assert.That(maxManagedMemory, Is.LessThan(200_000_000));
    }

    [Test]
    public async Task FileTable_FirstRowsMatchSqlite()
    {
        const int rows = 1000;
        var expected = RunSqlite($"SELECT rowid, sha256, sha1, md5, crc32, file_name, file_size, package_id FROM FILE ORDER BY rowid LIMIT {rows}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await using var database = await SqliteDatabase.OpenAsync(DatabasePath);

        var actual = new List<string>();
        await foreach (var row in database.ReadTableAsync("FILE"))
        {
            actual.Add(string.Join('|', new object?[] { row.RowId }.Concat(row.Values)));
            if (actual.Count == rows)
            {
                break;
            }
        }

        Assert.That(actual, Is.EqualTo(expected));
    }

    private static long SqliteCount(string table) => long.Parse(RunSqlite($"SELECT count(*) FROM \"{table}\"").Trim());

    private static string RunSqlite(string sql)
    {
        var info = new ProcessStartInfo("sqlite3") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-readonly");
        info.ArgumentList.Add(DatabasePath);
        info.ArgumentList.Add(sql);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, "sqlite3 failed");
        return output;
    }

    [GeneratedRegex(@"CREATE TABLE (\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableRegex();
}
