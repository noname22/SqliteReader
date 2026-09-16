namespace SqliteReader.Tests;

public class OptionsTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp() => _tempDirectory = TestData.CreateTempDirectory();

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    [Test]
    public void Defaults_BehaveLikeSqlite()
    {
        var options = SqliteDatabaseOptions.Default;

        Assert.Multiple(() =>
        {
            Assert.That(options.UseWalFile, Is.True);
            Assert.That(options.CheckHotJournal, Is.True);
            Assert.That(options.MaxWalSize, Is.Null);
            Assert.That(options.MaxRowSize, Is.EqualTo(1_000_000_000));
            Assert.That(new SqliteDatabaseOptions(), Is.EqualTo(options));
        });
    }

    [Test]
    public void InvalidLimits_Throw()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SqliteDatabaseOptions { MaxRowSize = 0 });
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SqliteDatabaseOptions { MaxRowSize = -1 });
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SqliteDatabaseOptions { MaxWalSize = -1 });
            Assert.DoesNotThrow(() => _ = new SqliteDatabaseOptions { MaxWalSize = 0 });
        });
    }

    [Test]
    public async Task NullOptions_UseDefaults()
    {
        await using var fromFile = await SqliteDatabase.OpenAsync(TestData.Path("wal.db"), options: null);
        await using var fromStream = await SqliteDatabase.OpenStreamAsync(
            ReadToMemory("wal.db"), ReadToMemory("wal.db-wal"), options: null);

        Assert.That(fromFile.Tables, Has.Count.EqualTo(2));
        Assert.That(fromStream.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CheckHotJournalDisabled_OpensDespiteJournal()
    {
        string path = Path.Combine(_tempDirectory, "journal.db");
        File.Copy(TestData.Path("types.db"), path);
        File.WriteAllBytes(path + "-journal", [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7]);

        Assert.ThrowsAsync<NotSupportedException>(() => SqliteDatabase.OpenAsync(path));
        await using var database = await SqliteDatabase.OpenAsync(path, new SqliteDatabaseOptions { CheckHotJournal = false });

        Assert.That(database.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task UseWalFileDisabled_DoesNotApplyToStreams()
    {
        var options = new SqliteDatabaseOptions { UseWalFile = false };

        await using var database = await SqliteDatabase.OpenStreamAsync(ReadToMemory("wal.db"), ReadToMemory("wal.db-wal"), options);

        Assert.That(database.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public void MaxWalSize_SmallerThanLog_Throws()
    {
        long walSize = new FileInfo(TestData.Path("wal.db-wal")).Length;
        var options = new SqliteDatabaseOptions { MaxWalSize = walSize - 1 };

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => SqliteDatabase.OpenAsync(TestData.Path("wal.db"), options));
        Assert.That(exception!.Message, Does.Contain(nameof(SqliteDatabaseOptions.MaxWalSize)));
        Assert.ThrowsAsync<NotSupportedException>(
            () => SqliteDatabase.OpenStreamAsync(ReadToMemory("wal.db"), ReadToMemory("wal.db-wal"), options));
    }

    [Test]
    public async Task MaxWalSize_EqualToLog_Opens()
    {
        long walSize = new FileInfo(TestData.Path("wal.db-wal")).Length;

        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("wal.db"),
            new SqliteDatabaseOptions { MaxWalSize = walSize });

        Assert.That(database.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task MaxWalSize_DoesNotApplyWhenWalIsNotUsed()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("wal.db"),
            new SqliteDatabaseOptions { MaxWalSize = 0, UseWalFile = false });

        Assert.That(database.Tables, Is.Empty);
    }

    [Test]
    public async Task MaxRowSize_RowsUpToTheLimitAreRead()
    {
        // overflow4096.db has rows with text and blob columns of 'len' bytes each, up to 1 MB.
        var options = new SqliteDatabaseOptions { MaxRowSize = 50_000 };
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("overflow4096.db"), options);
        long lastLength = 0;

        var exception = Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var row in database.ReadTableAsync("t"))
            {
                lastLength = (long)row["len"]!;
            }
        });

        Assert.That(exception!.Message, Does.Contain(nameof(SqliteDatabaseOptions.MaxRowSize)));
        Assert.That(lastLength, Is.GreaterThan(4000).And.LessThan(25_000));
    }

    [Test]
    public async Task MaxRowSize_AppliesToWithoutRowIdKeys()
    {
        var options = new SqliteDatabaseOptions { MaxRowSize = 1000 };
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("overflow4096.db"), options);

        Assert.ThrowsAsync<NotSupportedException>(() => database.ReadAllAsync("w"));
    }

    [Test]
    public async Task MaxRowSize_LargeEnough_ReadsEverything()
    {
        var options = new SqliteDatabaseOptions { MaxRowSize = 2_000_100 };
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("overflow4096.db"), options);

        Assert.That((await database.ReadAllAsync("t")).Max(r => (long)r["len"]!), Is.EqualTo(1_000_000));
    }

    [Test]
    public void MaxRowSize_AlsoAppliesToTheSchema()
    {
        var options = new SqliteDatabaseOptions { MaxRowSize = 10 };

        Assert.ThrowsAsync<NotSupportedException>(() => SqliteDatabase.OpenAsync(TestData.Path("types.db"), options));
    }

    [Test]
    public async Task Options_WithExpression_CopiesValues()
    {
        var strict = SqliteDatabaseOptions.Default with { MaxRowSize = 1234, UseWalFile = false };

        Assert.That(strict.MaxRowSize, Is.EqualTo(1234));
        Assert.That(SqliteDatabaseOptions.Default.MaxRowSize, Is.EqualTo(SqliteDatabaseOptions.DefaultMaxRowSize));
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("types.db"), strict);
        Assert.That(database.Tables, Has.Count.EqualTo(2));
    }

    private static MemoryStream ReadToMemory(string fileName) => new(File.ReadAllBytes(TestData.Path(fileName)));
}
