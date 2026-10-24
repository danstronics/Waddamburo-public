using System.Buffers.Binary;
using System.Text;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Formats.Tests;

public sealed class DdpArchiveEditorTests
{
    [Fact]
    public void NoOpAndConservativeExportPreserveEveryOtherByte()
    {
        var source = Archive(); var archive = DdpArchive.Open(source);
        var original = archive.OpenMovie("first").Data;
        Assert.Equal(source, DdpArchiveEditor.ReplaceMovie(source, "first", original, true));
        var document = new LmbEditDocument(original.Span);
        document.SetPlacement(2, 0, 1, 1, new LmbMatrix(2, 0, 0, 2, 90, 80));
        var result = DdpArchiveEditor.ReplaceMovie(source, "first", document.Export(true), true);
        Assert.Equal(source.Length, result.Length);
        var changedStart = (int)archive.Index.GetMovie("first").Offset;
        var changedEnd = changedStart + original.Length;
        Assert.Equal(source[..changedStart], result[..changedStart]);
        Assert.Equal(source[changedEnd..], result[changedEnd..]);
    }

    [Fact]
    public void RebuiltExportRelocatesFollowingMoviesWhileRetainingGapsTexturesAndTrailer()
    {
        var source = Archive(); var before = DdpArchive.Open(source);
        var document = new LmbEditDocument(before.OpenMovie("first").Data.Span);
        document.SetPlacement(2, 1, 12, 1, new LmbMatrix(1, 0, 0, 1, 44, 55), name: "new_element");
        var movie = document.Export(false);
        Assert.Throws<InvalidOperationException>(() => DdpArchiveEditor.ReplaceMovie(source, "first", movie, true));
        var result = DdpArchiveEditor.ReplaceMovie(source, "first", movie, false);
        var after = DdpArchive.Open(result);
        Assert.Equal(movie, after.OpenMovie("first").Data.ToArray());
        Assert.Equal(before.OpenMovie("second").Data.ToArray(), after.OpenMovie("second").Data.ToArray());
        Assert.Equal(before.OpenMovie("second").Textures[0].Data.ToArray(), after.OpenMovie("second").Textures[0].Data.ToArray());
        Assert.Equal(4096, after.Index.GetMovie("second").Offset - before.Index.GetMovie("second").Offset);
        Assert.Equal(source[^3..], result[^3..]);
        Assert.Equal(before.Index.TrailerUnknown, after.Index.TrailerUnknown);
        Assert.Equal(0xA5, result[(int)after.Index.GetMovie("second").Offset - 1]);
    }

    [Fact]
    public void PixelReplacementKeepsHeadersAndRejectsOnePixelLargerTexture()
    {
        var source = Archive(); var nut = Nut(2, 2, 0xFF);
        var result = DdpArchiveEditor.ReplaceTexture(source, 0, nut);
        var entry = DdpArchive.Open(source).Index.Textures[0];
        Assert.Equal(source[..((int)entry.Offset + 0x60)], result[..((int)entry.Offset + 0x60)]);
        Assert.Equal(source[(int)(entry.Offset + entry.Length)..], result[(int)(entry.Offset + entry.Length)..]);
        Assert.Throws<InvalidOperationException>(() => DdpArchiveEditor.ReplaceTexture(source, 0, Nut(3, 2, 0)));
    }

    internal static byte[] Archive()
    {
        var movie = LmbEditDocumentTests.Movie(); var nut = Nut(2, 2, 0x11);
        using var stream = new MemoryStream();
        void word(uint value) => LmbEditDocumentTests.Word(stream, value);
        stream.Write("LM_NUT_TYPE1"u8); stream.Write(new byte[4]); word(2); stream.Write(new byte[] { 0xB1, 0xB2 }); stream.Write(new byte[20]);
        word(2); stream.Write(new byte[9]);
        foreach (var name in new[] { "first", "second" })
        {
            var text = Encoding.UTF8.GetBytes(name); word((uint)text.Length); stream.Write(text);
            if (name == "first") stream.Write(new byte[5]);
            word(name == "first" ? 0u : (uint)movie.Length + 16); word((uint)movie.Length); word(0); word(1);
        }
        stream.Write(new byte[5]); word(1); stream.Write(new byte[9]); word(0); word((uint)nut.Length);
        word((uint)movie.Length * 2 + 16); word((uint)nut.Length); word(0x12345678); word(99);
        stream.Write(movie); stream.Write(Enumerable.Repeat((byte)0xA5, 16).ToArray()); stream.Write(movie); stream.Write(nut); stream.Write(new byte[] { 8, 9, 10 });
        return stream.ToArray();
    }

    internal static byte[] Nut(ushort width, ushort height, byte fill)
    {
        var bytes = new byte[0x60 + width * height * 4]; "NTP3"u8.CopyTo(bytes); bytes[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x10), (uint)(0x50 + width * height * 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x18), (uint)(width * height * 4));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x1C), 0x50); bytes[0x21] = 1; bytes[0x23] = 14;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x24), width); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x26), height);
        bytes.AsSpan(0x60).Fill(fill); return bytes;
    }
}
