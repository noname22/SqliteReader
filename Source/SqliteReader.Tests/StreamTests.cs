namespace SqliteReader.Tests;

public class StreamTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp() => _tempDirectory = TestData.CreateTempDirectory();

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    [Test]
    public async Task OpenStream_WithWal_IncludesTheLog()
    {
        await using var database = await SqliteDatabase.OpenStreamAsync(ReadToMemory("wal.db"), ReadToMemory("wal.db-wal"));

        Assert.That(database.Tables.Select(t => t.Name), Is.EqualTo(new[] { "t", "w" }));
        Assert.That((await database.ReadAllAsync("t")).Last()["v"], Is.EqualTo("last"));
    }

    [Test]
    public async Task OpenStream_WithoutWal_ReadsOnlyTheDatabase()
    {
        await using var database = await SqliteDatabase.OpenStreamAsync(ReadToMemory("wal.db"));

        Assert.That(database.Tables, Is.Empty);
    }

    [Test]
    public async Task OpenFile_WithoutUsingWalFile_ReadsOnlyTheDatabase()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("wal.db"), useWalFile: false);

        Assert.That(database.Tables, Is.Empty);
    }

    [Test]
    public async Task OpenFile_UsingWalFile_IncludesTheLog()
    {
        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("wal.db"), useWalFile: true);

        Assert.That(database.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public void OpenFile_WithoutUsingWalFile_StillRejectsHotJournal()
    {
        string path = Path.Combine(_tempDirectory, "journal.db");
        File.Copy(TestData.Path("types.db"), path);
        File.WriteAllBytes(path + "-journal", [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7]);

        Assert.ThrowsAsync<NotSupportedException>(() => SqliteDatabase.OpenAsync(path, useWalFile: false));
    }

    [Test]
    public async Task OpenStream_FileStream_Works()
    {
        await using var database = await SqliteDatabase.OpenStreamAsync(File.OpenRead(TestData.Path("multipage.db")));

        Assert.That(await database.ReadAllAsync("t"), Has.Count.GreaterThan(1000));
    }

    [Test]
    public async Task OpenStream_DoesNotDependOnStreamPosition()
    {
        var stream = ReadToMemory("types.db");
        stream.Position = stream.Length;
        await using var database = await SqliteDatabase.OpenStreamAsync(stream);
        stream.Position = 5;

        Assert.That(await database.ReadAllAsync("vals"), Has.Count.EqualTo(49));
    }

    [Test]
    public async Task OpenStream_DisposesStreamsByDefault()
    {
        var stream = ReadToMemory("wal.db");
        var wal = ReadToMemory("wal.db-wal");

        var database = await SqliteDatabase.OpenStreamAsync(stream, wal);
        Assert.That(stream.CanRead && wal.CanRead, Is.True);
        await database.DisposeAsync();

        Assert.That(stream.CanRead || wal.CanRead, Is.False);
    }

    [Test]
    public async Task OpenStream_DisposesUnusedWalStream()
    {
        var wal = new MemoryStream();

        var database = await SqliteDatabase.OpenStreamAsync(ReadToMemory("types.db"), wal);
        await database.DisposeAsync();

        Assert.That(wal.CanRead, Is.False);
    }

    [Test]
    public async Task OpenStream_LeaveOpen_KeepsStreamsOpen()
    {
        var stream = ReadToMemory("wal.db");
        var wal = ReadToMemory("wal.db-wal");

        var database = await SqliteDatabase.OpenStreamAsync(stream, wal, leaveOpen: true);
        var rows = database.ReadTableAsync("t");
        await database.DisposeAsync();

        Assert.That(stream.CanRead && wal.CanRead, Is.True);
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in rows)
            {
            }
        });
    }

    [Test]
    public void OpenStream_InvalidDatabase_DisposesStreams()
    {
        var stream = new MemoryStream(new byte[4096]);

        Assert.ThrowsAsync<SqliteFormatException>(() => SqliteDatabase.OpenStreamAsync(stream));
        Assert.That(stream.CanRead, Is.False);
    }

    [Test]
    public void OpenStream_InvalidDatabaseWithLeaveOpen_KeepsStreamsOpen()
    {
        var stream = new MemoryStream(new byte[4096]);
        var wal = new MemoryStream();

        Assert.ThrowsAsync<SqliteFormatException>(() => SqliteDatabase.OpenStreamAsync(stream, wal, leaveOpen: true));
        Assert.That(stream.CanRead && wal.CanRead, Is.True);
    }

    [Test]
    public void OpenStream_NullDatabase_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => SqliteDatabase.OpenStreamAsync(null!));
    }

    [Test]
    public void OpenStream_NonSeekableStream_Throws()
    {
        var exception = Assert.ThrowsAsync<ArgumentException>(
            () => SqliteDatabase.OpenStreamAsync(new NonSeekableStream(ReadToMemory("types.db"))));
        Assert.That(exception!.ParamName, Is.EqualTo("database"));
    }

    [Test]
    public void OpenStream_NonSeekableWal_Throws()
    {
        var exception = Assert.ThrowsAsync<ArgumentException>(
            () => SqliteDatabase.OpenStreamAsync(ReadToMemory("wal.db"), new NonSeekableStream(ReadToMemory("wal.db-wal"))));
        Assert.That(exception!.ParamName, Is.EqualTo("wal"));
    }

    [Test]
    public void OpenStream_WriteOnlyStream_Throws()
    {
        string path = Path.Combine(_tempDirectory, "write-only.db");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);

        Assert.ThrowsAsync<ArgumentException>(() => SqliteDatabase.OpenStreamAsync(stream, leaveOpen: true));
    }

    [Test]
    public async Task OpenStream_ConcurrentEnumerationsOverMemoryStream_ProduceSameResults()
    {
        await using var database = await SqliteDatabase.OpenStreamAsync(ReadToMemory("multipage.db"));
        var expected = (await database.ReadAllAsync("t")).Select(r => r.Values).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            (await database.ReadAllAsync("t")).Select(r => r.Values).ToList())));

        Assert.Multiple(() =>
        {
            foreach (var result in results)
            {
                Assert.That(result, Is.EqualTo(expected));
            }
        });
    }

    private static MemoryStream ReadToMemory(string fileName) => new(File.ReadAllBytes(TestData.Path(fileName)));

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
