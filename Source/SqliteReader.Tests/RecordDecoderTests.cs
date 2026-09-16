using System.Text;
using SqliteReader.Internal;

namespace SqliteReader.Tests;

public class RecordDecoderTests
{
    [Test]
    public void Decode_AllSerialTypes()
    {
        byte[] record =
        [
            // Header: size 14, then serial types 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12+2*2 (blob), 13+2*3 (text), 12 (empty blob)
            14, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 16, 19, 12,
            0xFF,
            0x80, 0x00,
            0xFF, 0xFF, 0xFE,
            0x7F, 0xFF, 0xFF, 0xFF,
            0x80, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18,
            0xCA, 0xFE,
            (byte)'a', (byte)'b', (byte)'c',
        ];

        var values = RecordDecoder.Decode(record, Encoding.UTF8);

        Assert.That(values, Is.EqualTo(new object?[]
        {
            null, -1L, -32768L, -2L, int.MaxValue, -(1L << 47) + 1, 0x0102030405060708L, Math.PI, 0L, 1L,
            new byte[] { 0xCA, 0xFE }, "abc", Array.Empty<byte>(),
        }));
        Assert.That(values[1], Is.TypeOf<long>());
    }

    [Test]
    public void Decode_Utf16Text()
    {
        byte[] le = [2, 13 + 2 * 4, (byte)'h', 0, (byte)'i', 0];
        byte[] be = [2, 13 + 2 * 4, 0, (byte)'h', 0, (byte)'i'];

        Assert.Multiple(() =>
        {
            Assert.That(RecordDecoder.Decode(le, Encoding.Unicode), Is.EqualTo(new object[] { "hi" }));
            Assert.That(RecordDecoder.Decode(be, Encoding.BigEndianUnicode), Is.EqualTo(new object[] { "hi" }));
        });
    }

    [Test]
    public void Decode_StopsAtMaxFields()
    {
        byte[] record = [3, 1, 1, 5, 6];
        var sink = new CountingSink();

        int count = RecordDecoder.Decode(record, Encoding.UTF8, 1, ref sink);

        Assert.That(count, Is.EqualTo(1));
    }

    [TestCase(new byte[] { 5, 0 }, TestName = "Header larger than record")]
    [TestCase(new byte[] { 2, 4, 1 }, TestName = "Field beyond end of record")]
    [TestCase(new byte[] { 2, 10 }, TestName = "Reserved serial type 10")]
    [TestCase(new byte[] { 2, 11 }, TestName = "Reserved serial type 11")]
    [TestCase(new byte[] { 0 }, TestName = "Header size smaller than its own varint")]
    [TestCase(new byte[] { 4, 0xFA, 0x89, 0x0A }, TestName = "Blob larger than the record")]
    [TestCase(new byte[] { 6, 0x90, 0x80, 0x80, 0x80, 0x0D }, TestName = "Text larger than int.MaxValue")]
    [TestCase(new byte[] { 6, 0x87, 0xB9, 0xD6, 0xA8, 0x0E }, TestName = "Blob of over a gigabyte in a small record")]
    public void Decode_RejectsCorruptRecords(byte[] record)
    {
        Assert.Throws<SqliteFormatException>(() => RecordDecoder.Decode(record, Encoding.UTF8));
    }

    private struct CountingSink : RecordDecoder.IFieldSink
    {
        public void Set(int index, object? value)
        {
        }
    }
}
