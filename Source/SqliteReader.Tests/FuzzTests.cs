namespace SqliteReader.Tests;

/// <summary>
/// Reads copies of the test databases with random bytes changed. A corrupt file may return wrong data, but it must
/// only ever fail with <see cref="SqliteFormatException"/> or <see cref="NotSupportedException"/>, and never hang.
/// Set FUZZ_ITERATIONS and FUZZ_SEED to run longer or different campaigns.
/// </summary>
public class FuzzTests
{
    private static readonly int Iterations =
        int.TryParse(Environment.GetEnvironmentVariable("FUZZ_ITERATIONS"), out int iterations) ? iterations : 150;

    private static readonly int Seed =
        int.TryParse(Environment.GetEnvironmentVariable("FUZZ_SEED"), out int seed) ? seed : 12345;

    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp() => _tempDirectory = TestData.CreateTempDirectory();

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    [TestCase("types.db", null)]
    [TestCase("schema.db", null)]
    [TestCase("corruptible.db", null)]
    [TestCase("multipage.db", null)]
    [TestCase("utf16be.db", null)]
    [TestCase("wal.db", "wal.db-wal")]
    public async Task RandomlyCorruptedFile_FailsCleanly(string file, string? wal)
    {
        byte[] original = File.ReadAllBytes(TestData.Path(wal ?? file));
        string path = Path.Combine(_tempDirectory, file);
        if (wal is not null)
        {
            File.Copy(TestData.Path(file), path);
        }

        string target = wal is null ? path : path + "-wal";
        var random = new Random(file.Sum(c => c) ^ Seed);
        int failures = 0;

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            byte[] corrupt = (byte[])original.Clone();
            int changes = random.Next(1, 30);
            for (int i = 0; i < changes; i++)
            {
                // Mostly hit the first pages, where the header, schema and b-tree roots are.
                int limit = random.Next(2) == 0 ? Math.Min(corrupt.Length, 4096) : corrupt.Length;
                corrupt[random.Next(limit)] = (byte)random.Next(256);
            }

            File.WriteAllBytes(target, corrupt);
            try
            {
                await ReadEverythingAsync(path);
            }
            catch (Exception e) when (e is SqliteFormatException or NotSupportedException)
            {
                failures++;
            }
            catch (Exception e)
            {
                Assert.Fail($"Iteration {iteration} threw {e.GetType().Name}: {e}");
            }
        }

        TestContext.Out.WriteLine($"{failures} of {Iterations} corrupted copies were rejected.");
    }

    private static async Task ReadEverythingAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var database = await SqliteDatabase.OpenAsync(path, timeout.Token);
        foreach (string table in database.Tables.Select(t => t.Name).Append("sqlite_schema"))
        {
            await foreach (var row in database.ReadTableAsync(table, timeout.Token))
            {
                _ = row["" + row.Table.Columns[0]];
            }
        }
    }
}
