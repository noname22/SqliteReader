using SqliteReader.Internal;

namespace SqliteReader.Tests;

public class VarintTests
{
    [TestCase(new byte[] { 0x00 }, 0L, 1)]
    [TestCase(new byte[] { 0x7F }, 127L, 1)]
    [TestCase(new byte[] { 0x81, 0x00 }, 128L, 2)]
    [TestCase(new byte[] { 0xFF, 0x7F }, 16383L, 2)]
    [TestCase(new byte[] { 0x81, 0x80, 0x00 }, 16384L, 3)]
    [TestCase(new byte[] { 0x87, 0xFF, 0xFF, 0xFF, 0x7F }, 0x7FFFFFFFL, 5)]
    [TestCase(new byte[] { 0x81, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 }, 1L << 57, 9)]
    [TestCase(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, -1L, 9)]
    [TestCase(new byte[] { 0xBF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, long.MaxValue, 9)]
    [TestCase(new byte[] { 0x05, 0xFF }, 5L, 1)]
    public void Read_DecodesValueAndLength(byte[] bytes, long expected, int expectedLength)
    {
        int length = Varint.Read(bytes, out long value);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo(expected));
            Assert.That(length, Is.EqualTo(expectedLength));
        });
    }

    [TestCase(new byte[0])]
    [TestCase(new byte[] { 0x80 })]
    [TestCase(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 })]
    public void Read_ThrowsOnTruncatedInput(byte[] bytes)
    {
        Assert.Throws<SqliteFormatException>(() => Varint.Read(bytes, out _));
    }
}
