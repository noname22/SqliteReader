using System.Buffers.Binary;
using SqliteReader.Internal;

namespace SqliteReader.Tests;

/// <summary>
/// Reads modified copies of corruptible.db. Corrupt files must fail with <see cref="SqliteFormatException"/>
/// rather than hang, allocate huge buffers or throw other exception types.
/// </summary>
public class CorruptionTests
{
    private const int PageSize = 512;

    private string _tempDirectory = null!;
    private string _path = null!;
    private byte[] _file = null!;
    private uint _bigRoot;
    private uint _treeRoot;

    [SetUp]
    public async Task SetUp()
    {
        _tempDirectory = TestData.CreateTempDirectory();
        _path = Path.Combine(_tempDirectory, "corrupt.db");
        _file = File.ReadAllBytes(TestData.Path("corruptible.db"));

        await using var database = await SqliteDatabase.OpenAsync(TestData.Path("corruptible.db"));
        _bigRoot = database.Tables.Single(t => t.Name == "big").RootPage;
        _treeRoot = database.Tables.Single(t => t.Name == "tree").RootPage;
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    [Test]
    public void TestDatabase_HasExpectedLayout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Page(_treeRoot)[0], Is.EqualTo(0x05), "tree root is an interior page");
            Assert.That(Page(ChildOfCell(_treeRoot, 0))[0], Is.EqualTo(0x05), "tree has interior pages below the root");
            Assert.That(Page(_bigRoot)[0], Is.EqualTo(0x0D), "big root is a leaf page");
            Assert.That(CellCount(_bigRoot), Is.EqualTo(1));
        });
    }

    [Test]
    public void ChildReferencedTwice_Throws()
    {
        SetChildOfCell(_treeRoot, 1, ChildOfCell(_treeRoot, 0));

        var exception = AssertReadThrows("tree");
        Assert.That(exception.Message, Does.Contain("more than once"));
    }

    [Test]
    public void ChildPointingBackToRoot_Throws()
    {
        BinaryPrimitives.WriteUInt32BigEndian(Page(_treeRoot)[8..], _treeRoot);

        var exception = AssertReadThrows("tree");
        Assert.That(exception.Message, Does.Contain("more than once"));
    }

    [Test]
    public void EveryInteriorCellPointingToTheSameSubtree_ThrowsQuickly()
    {
        // Without cycle detection, making every cell of every interior page point to the same child multiplies the
        // number of visited pages at each level.
        uint child = ChildOfCell(_treeRoot, 0);
        uint grandchild = ChildOfCell(child, 0);
        for (int i = 0; i < CellCount(_treeRoot); i++)
        {
            SetChildOfCell(_treeRoot, i, child);
        }

        for (int i = 0; i < CellCount(child); i++)
        {
            SetChildOfCell(child, i, grandchild);
        }

        AssertReadThrows("tree");
    }

    [Test]
    public void CellPointerAtEndOfPage_Throws()
    {
        BinaryPrimitives.WriteUInt16BigEndian(Page(_treeRoot)[12..], PageSize - 2);

        AssertReadThrows("tree");
    }

    [Test]
    public void CellCountBeyondPage_Throws()
    {
        BinaryPrimitives.WriteUInt16BigEndian(Page(_treeRoot)[3..], 1000);

        AssertReadThrows("tree");
    }

    [Test]
    public void PayloadSizeLargerThanTheFileCouldHold_Throws()
    {
        int cell = CellOffset(_bigRoot, 0);
        Varint.Read(Page(_bigRoot)[cell..], out long originalSize);
        Assert.That(Varint.Read(Page(_bigRoot)[cell..], out _), Is.EqualTo(3), "payload size is a 3-byte varint");
        Assert.That(originalSize, Is.LessThan(0x1FFFFF));

        // The largest 3-byte varint: 2097151 bytes would need about 4000 overflow pages.
        Page(_bigRoot)[cell] = 0xFF;
        Page(_bigRoot)[cell + 1] = 0xFF;
        Page(_bigRoot)[cell + 2] = 0x7F;

        var exception = AssertReadThrows("big");
        Assert.That(exception.Message, Does.Contain("overflow pages"));
    }

    [Test]
    public void OverflowChainLoop_Throws()
    {
        uint first = FirstOverflowPage();
        uint second = BinaryPrimitives.ReadUInt32BigEndian(Page(first));
        Assert.That(second, Is.Not.Zero);
        BinaryPrimitives.WriteUInt32BigEndian(Page(second), first);

        var exception = AssertReadThrows("big");
        Assert.That(exception.Message, Does.Contain("loops"));
    }

    [Test]
    public void OverflowChainEndingEarly_Throws()
    {
        BinaryPrimitives.WriteUInt32BigEndian(Page(FirstOverflowPage()), 0);

        var exception = AssertReadThrows("big");
        Assert.That(exception.Message, Does.Contain("prematurely"));
    }

    [Test]
    public void OverflowPointerBeyondFile_Throws()
    {
        BinaryPrimitives.WriteUInt32BigEndian(Page(FirstOverflowPage()), 100_000);

        AssertReadThrows("big");
    }

    [Test]
    public async Task UnmodifiedCopy_ReadsFine()
    {
        File.WriteAllBytes(_path, _file);
        await using var database = await SqliteDatabase.OpenAsync(_path);

        Assert.That((byte[])(await database.ReadAllAsync("big"))[0]["v"]!, Has.Length.EqualTo(20000));
        Assert.That(await database.ReadAllAsync("tree"), Has.Count.EqualTo(2000));
    }

    private SqliteFormatException AssertReadThrows(string table)
    {
        File.WriteAllBytes(_path, _file);
        return Assert.ThrowsAsync<SqliteFormatException>(async () =>
        {
            await using var database = await SqliteDatabase.OpenAsync(_path);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var _ in database.ReadTableAsync(table, timeout.Token))
            {
            }
        })!;
    }

    private Span<byte> Page(uint pageNumber) => _file.AsSpan((int)(pageNumber - 1) * PageSize, PageSize);

    private int HeaderOffset(uint pageNumber) => pageNumber == 1 ? 100 : 0;

    private int CellCount(uint pageNumber) =>
        BinaryPrimitives.ReadUInt16BigEndian(Page(pageNumber)[(HeaderOffset(pageNumber) + 3)..]);

    private int CellOffset(uint pageNumber, int cell)
    {
        var page = Page(pageNumber);
        int headerSize = page[HeaderOffset(pageNumber)] is 0x05 or 0x02 ? 12 : 8;
        return BinaryPrimitives.ReadUInt16BigEndian(page[(HeaderOffset(pageNumber) + headerSize + cell * 2)..]);
    }

    private uint ChildOfCell(uint pageNumber, int cell) =>
        BinaryPrimitives.ReadUInt32BigEndian(Page(pageNumber)[CellOffset(pageNumber, cell)..]);

    private void SetChildOfCell(uint pageNumber, int cell, uint child) =>
        BinaryPrimitives.WriteUInt32BigEndian(Page(pageNumber)[CellOffset(pageNumber, cell)..], child);

    /// <summary>Finds the first overflow page of the single cell in 'big', using SQLite's local size formula.</summary>
    private uint FirstOverflowPage()
    {
        var page = Page(_bigRoot);
        int pos = CellOffset(_bigRoot, 0);
        pos += Varint.Read(page[pos..], out long payloadSize);
        pos += Varint.Read(page[pos..], out _);

        const int usable = PageSize;
        int maxLocal = usable - 35;
        int minLocal = (usable - 12) * 32 / 255 - 23;
        int surplus = (int)(minLocal + (payloadSize - minLocal) % (usable - 4));
        int local = surplus <= maxLocal ? surplus : minLocal;
        return BinaryPrimitives.ReadUInt32BigEndian(page[(pos + local)..]);
    }
}
