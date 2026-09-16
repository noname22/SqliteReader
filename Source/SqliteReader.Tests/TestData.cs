namespace SqliteReader.Tests;

internal static class TestData
{
    public static string Path(string fileName) =>
        System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", fileName);

    /// <summary>Creates an empty, uniquely named directory for tests that need to write files.</summary>
    public static string CreateTempDirectory()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SqliteReaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static async Task<List<SqliteRow>> ReadAllAsync(this SqliteDatabase database, string table)
    {
        var rows = new List<SqliteRow>();
        await foreach (var row in database.ReadTableAsync(table))
        {
            rows.Add(row);
        }

        return rows;
    }
}
