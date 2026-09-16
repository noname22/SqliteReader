namespace SqliteReader.Tests;

public class DatabaseTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp() => _tempDirectory = TestData.CreateTempDirectory();

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    [Test]
    public void Open_MissingFile_Throws()
    {
        Assert.ThrowsAsync<FileNotFoundException>(() => SqliteDatabase.OpenAsync(Path.Combine(_tempDirectory, "missing.db")));
    }

    [Test]
    public void Open_NotADatabase_Throws()
    {
        string path = Path.Combine(_tempDirectory, "text.db");
        File.WriteAllText(path, new string('x', 4096));

        Assert.ThrowsAsync<SqliteFormatException>(() => SqliteDatabase.OpenAsync(path));
    }

    [Test]
    public void Open_TooShortForHeader_Throws()
    {
        string path = Path.Combine(_tempDirectory, "short.db");
        File.WriteAllBytes(path, File.ReadAllBytes(TestData.Path("types.db"))[..50]);

        Assert.ThrowsAsync<SqliteFormatException>(() => SqliteDatabase.OpenAsync(path));
    }

    [Test]
    public async Task Open_EmptyFile_HasNoTables()
    {
        string path = Path.Combine(_tempDirectory, "empty.db");
        File.WriteAllBytes(path, []);

        await using var database = await SqliteDatabase.OpenAsync(path);

        Assert.That(database.Tables, Is.Empty);
        Assert.That(await database.ReadAllAsync("sqlite_schema"), Is.Empty);
    }

    [Test]
    public async Task Read_TruncatedFile_Throws()
    {
        string path = Path.Combine(_tempDirectory, "truncated.db");
        File.WriteAllBytes(path, File.ReadAllBytes(TestData.Path("multipage.db"))[..(512 * 40)]);

        await using var database = await SqliteDatabase.OpenAsync(path);

        Assert.ThrowsAsync<SqliteFormatException>(() => database.ReadAllAsync("t"));
    }

    [Test]
    public void Open_UncheckpointedWal_ThrowsNotSupported()
    {
        var exception = Assert.ThrowsAsync<NotSupportedException>(() => SqliteDatabase.OpenAsync(TestData.Path("wal.db")));
        Assert.That(exception!.Message, Does.Contain("write-ahead log"));
    }

    [Test]
    public async Task Open_WalModeWithEmptyWalFile_Succeeds()
    {
        string path = Path.Combine(_tempDirectory, "wal.db");
        File.Copy(TestData.Path("wal-checkpointed.db"), path);
        File.WriteAllBytes(path + "-wal", []);

        await using var database = await SqliteDatabase.OpenAsync(path);

        Assert.That((await database.ReadAllAsync("t")).Select(r => r[0]), Is.EqualTo(new object[] { 1L, 2L, 3L }));
    }

    [Test]
    public void Open_HotJournal_ThrowsNotSupported()
    {
        string path = Path.Combine(_tempDirectory, "journal.db");
        File.Copy(TestData.Path("types.db"), path);
        File.WriteAllBytes(path + "-journal", [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7, 0, 0, 0, 0]);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => SqliteDatabase.OpenAsync(path));
        Assert.That(exception!.Message, Does.Contain("journal"));
    }

    [Test]
    public async Task Open_ZeroedJournal_Succeeds()
    {
        string path = Path.Combine(_tempDirectory, "journal.db");
        File.Copy(TestData.Path("types.db"), path);
        File.WriteAllBytes(path + "-journal", new byte[512]);

        await using var database = await SqliteDatabase.OpenAsync(path);

        Assert.That(database.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Open_DoesNotModifyTheFile()
    {
        string path = Path.Combine(_tempDirectory, "copy.db");
        File.Copy(TestData.Path("schema.db"), path);
        byte[] before = File.ReadAllBytes(path);
        var modified = File.GetLastWriteTimeUtc(path);

        await using (var database = await SqliteDatabase.OpenAsync(path))
        {
            foreach (var table in database.Tables)
            {
                await database.ReadAllAsync(table.Name);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified));
            Assert.That(Directory.GetFiles(_tempDirectory), Is.EqualTo(new[] { path }));
        });
    }

    [Test]
    public async Task Open_ReadOnlyFile_Succeeds()
    {
        string path = Path.Combine(_tempDirectory, "readonly.db");
        File.Copy(TestData.Path("types.db"), path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
        }

        try
        {
            await using var database = await SqliteDatabase.OpenAsync(path);
            Assert.That(await database.ReadAllAsync("vals"), Is.Not.Empty);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task ReadTable_UnknownTable_ThrowsImmediately()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("types.db"));

        Assert.Throws<KeyNotFoundException>(() => database.ReadTableAsync("nope"));
    }

    [Test]
    public async Task ReadTable_TableFromOtherDatabase_Throws()
    {
        await using var first = await SqliteDatabase.OpenAsync(TestData.Path("types.db"));
        await using var second = await SqliteDatabase.OpenAsync(TestData.Path("types.db"));

        Assert.Throws<ArgumentException>(() => second.ReadTableAsync(first.Tables[0]));
    }

    [Test]
    public async Task ReadTable_Cancellation_Throws()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("multipage.db"));
        using var cts = new CancellationTokenSource();
        int count = 0;

        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in database.ReadTableAsync("t", cts.Token))
            {
                if (++count == 10)
                {
                    cts.Cancel();
                }
            }
        });
        Assert.That(count, Is.EqualTo(10));
    }

    [Test]
    public async Task ReadTable_WithCancellationExtension_Throws()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("multipage.db"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in database.ReadTableAsync("t").WithCancellation(cts.Token))
            {
            }
        });
    }

    [Test]
    public async Task ReadTable_AfterDispose_Throws()
    {
        var database = await SqliteDatabase.OpenAsync(TestData.Path("multipage.db"));
        var rows = database.ReadTableAsync("t");
        await database.DisposeAsync();

        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in rows)
            {
            }
        });
    }

    [Test]
    public async Task ReadTable_ConcurrentEnumerations_ProduceSameResults()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("multipage.db"));
        var expected = (await database.ReadAllAsync("t")).Select(r => r.RowId).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            var ids = new List<long?>();
            await foreach (var row in database.ReadTableAsync(i % 2 == 0 ? "t" : "w"))
            {
                ids.Add(row.RowId);
            }

            return ids;
        })));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < results.Length; i += 2)
            {
                Assert.That(results[i], Is.EqualTo(expected));
                Assert.That(results[i + 1], Has.Count.EqualTo(4000));
            }
        });
    }

    [Test]
    public async Task ReadTable_CanBeEnumeratedRepeatedly()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("types.db"));
        var rows = database.ReadTableAsync("vals");

        int first = 0, second = 0;
        await foreach (var _ in rows)
        {
            first++;
        }

        await foreach (var _ in rows)
        {
            second++;
        }

        Assert.That(second, Is.EqualTo(first).And.GreaterThan(0));
    }
}
