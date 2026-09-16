using System.Buffers.Binary;
using SqliteReader.Internal;

namespace SqliteReader.Tests;

/// <summary>
/// Reads copies of wal.db whose write-ahead log has been modified. The last transaction in wal.db is a single
/// insert of the row 'last'.
/// </summary>
public class WalTests
{
    private string _tempDirectory = null!;
    private string _path = null!;
    private byte[] _wal = null!;
    private List<SqliteRow> _originalRows = null!;

    private int PageSize => (int)BinaryPrimitives.ReadUInt32BigEndian(_wal.AsSpan(8));

    private int FrameSize => WalIndex.FrameHeaderSize + PageSize;

    private int FrameCount => (_wal.Length - WalIndex.HeaderSize) / FrameSize;

    [SetUp]
    public async Task SetUp()
    {
        _tempDirectory = TestData.CreateTempDirectory();
        _path = Path.Combine(_tempDirectory, "wal.db");
        File.Copy(TestData.Path("wal.db"), _path);
        _wal = File.ReadAllBytes(TestData.Path("wal.db-wal"));

        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("wal.db"));
        _originalRows = await database.ReadAllAsync("t");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    [Test]
    public void TestLog_EndsWithASingleFrameTransaction()
    {
        var commits = CommitFrames();

        Assert.That(commits, Has.Count.GreaterThan(2));
        Assert.That(commits[^1], Is.EqualTo(FrameCount - 1));
        Assert.That(commits[^2], Is.EqualTo(FrameCount - 2));
        Assert.That(_originalRows[^1]["v"], Is.EqualTo("last"));
    }

    [Test]
    public async Task TruncatedLastCommitFrame_DropsLastTransaction()
    {
        WriteWal(_wal[..(FrameOffset(FrameCount - 1) + FrameSize - 1)]);

        await AssertRowsAsync(_originalRows[..^1]);
    }

    [Test]
    public async Task CorruptLastCommitFrame_DropsLastTransaction()
    {
        _wal[FrameOffset(FrameCount - 1) + WalIndex.FrameHeaderSize + 100] ^= 0xFF;
        WriteWal(_wal);

        await AssertRowsAsync(_originalRows[..^1]);
    }

    [Test]
    public async Task UncommittedFramesAfterLastCommit_AreIgnored()
    {
        // Turn the last commit frame into an ordinary frame. The checksum only covers the first 8 bytes of the
        // frame header, which includes the commit size, so the checksum has to be recomputed.
        BinaryPrimitives.WriteUInt32BigEndian(_wal.AsSpan(FrameOffset(FrameCount - 1) + 4), 0);
        RecomputeChecksums(bigEndian: false);
        WriteWal(_wal);

        await AssertRowsAsync(_originalRows[..^1]);
    }

    [Test]
    public async Task FrameWithWrongSalt_EndsTheLog()
    {
        _wal[FrameOffset(FrameCount - 1) + 8] ^= 0x01;
        WriteWal(_wal);

        await AssertRowsAsync(_originalRows[..^1]);
    }

    [Test]
    public async Task HeaderOnly_IsIgnored()
    {
        WriteWal(_wal[..WalIndex.HeaderSize]);

        await using var database = await SqliteDatabase.OpenAsync(_path);
        Assert.That(database.Tables, Is.Empty);
    }

    [Test]
    public async Task NoCommittedFrames_IsIgnored()
    {
        WriteWal(_wal[..(FrameOffset(CommitFrames()[0]) + FrameSize - 1)]);

        await using var database = await SqliteDatabase.OpenAsync(_path);
        Assert.That(database.Tables, Is.Empty);
    }

    [TestCase(0, TestName = "Bad magic")]
    [TestCase(10, TestName = "Invalid page size")]
    [TestCase(17, TestName = "Changed salt (header checksum mismatch)")]
    [TestCase(25, TestName = "Bad header checksum")]
    public async Task InvalidHeader_LogIsIgnored(int offset)
    {
        _wal[offset] ^= 0x01;
        WriteWal(_wal);

        await using var database = await SqliteDatabase.OpenAsync(_path);
        Assert.That(database.Tables, Is.Empty);
    }

    [Test]
    public void UnknownVersion_ThrowsNotSupported()
    {
        BinaryPrimitives.WriteUInt32BigEndian(_wal.AsSpan(4), WalIndex.Version + 1);
        RecomputeChecksums(bigEndian: false);
        WriteWal(_wal);

        Assert.ThrowsAsync<NotSupportedException>(() => SqliteDatabase.OpenAsync(_path));
    }

    [Test]
    public void PageSizeMismatch_Throws()
    {
        // Pretend the frames hold 512-byte pages; the checksums are recomputed so the log is valid by itself.
        BinaryPrimitives.WriteUInt32BigEndian(_wal.AsSpan(8), 512);
        var frames = new List<byte>(_wal[..WalIndex.HeaderSize]);
        var frame = new byte[WalIndex.FrameHeaderSize + 512];
        BinaryPrimitives.WriteUInt32BigEndian(frame, 1);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 1);
        _wal.AsSpan(16, 8).CopyTo(frame.AsSpan(8));
        frames.AddRange(frame);
        _wal = frames.ToArray();
        RecomputeChecksums(bigEndian: false);
        WriteWal(_wal);

        Assert.ThrowsAsync<SqliteFormatException>(() => SqliteDatabase.OpenAsync(_path));
    }

    [Test]
    public async Task BigEndianChecksums_AreAccepted()
    {
        BinaryPrimitives.WriteUInt32BigEndian(_wal, WalIndex.Magic | 1);
        RecomputeChecksums(bigEndian: true);
        WriteWal(_wal);

        await AssertRowsAsync(_originalRows);
    }

    [Test]
    public async Task LittleEndianChecksumsRecomputed_MatchSqlite()
    {
        byte[] original = (byte[])_wal.Clone();

        RecomputeChecksums(bigEndian: false);

        Assert.That(_wal, Is.EqualTo(original));
        WriteWal(_wal);
        await AssertRowsAsync(_originalRows);
    }

    [Test]
    public async Task LogIsReadAtOpen_LaterChangesAreNotSeen()
    {
        WriteWal(_wal);
        await using var database = await SqliteDatabase.OpenAsync(_path);

        // Appending frames after opening must not change what the open database sees.
        using (var stream = new FileStream(_path + "-wal", FileMode.Append))
        {
            stream.Write(new byte[FrameSize * 3]);
        }

        Assert.That((await database.ReadAllAsync("t")).Select(r => r.Values), Is.EqualTo(_originalRows.Select(r => r.Values)));
    }

    private async Task AssertRowsAsync(IEnumerable<SqliteRow> expected)
    {
        await using var database = await SqliteDatabase.OpenAsync(_path);
        var actual = await database.ReadAllAsync("t");

        Assert.That(actual.Select(r => r.RowId), Is.EqualTo(expected.Select(r => r.RowId)));
        Assert.That(actual.Select(r => r.Values), Is.EqualTo(expected.Select(r => r.Values)));
    }

    private int FrameOffset(int frame) => WalIndex.HeaderSize + frame * FrameSize;

    private List<int> CommitFrames() => Enumerable.Range(0, FrameCount)
        .Where(i => BinaryPrimitives.ReadUInt32BigEndian(_wal.AsSpan(FrameOffset(i) + 4)) != 0)
        .ToList();

    private void RecomputeChecksums(bool bigEndian)
    {
        uint s1 = 0, s2 = 0;
        WalIndex.Checksum(_wal.AsSpan(0, 24), bigEndian, ref s1, ref s2);
        BinaryPrimitives.WriteUInt32BigEndian(_wal.AsSpan(24), s1);
        BinaryPrimitives.WriteUInt32BigEndian(_wal.AsSpan(28), s2);

        for (int i = 0; i < FrameCount; i++)
        {
            int offset = FrameOffset(i);
            WalIndex.Checksum(_wal.AsSpan(offset, 8), bigEndian, ref s1, ref s2);
            WalIndex.Checksum(_wal.AsSpan(offset + WalIndex.FrameHeaderSize, PageSize), bigEndian, ref s1, ref s2);
            BinaryPrimitives.WriteUInt32BigEndian(_wal.AsSpan(offset + 16), s1);
            BinaryPrimitives.WriteUInt32BigEndian(_wal.AsSpan(offset + 20), s2);
        }
    }

    private void WriteWal(byte[] contents) => File.WriteAllBytes(_path + "-wal", contents);
}
