using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Tests;

public sealed class DdpArchiveWriterTests
{
    [Fact]
    public void RebuildWithoutChangesIsByteIdentical()
    {
        var source = DdpArchiveEditorTests.Archive();
        Assert.Equal(source, DdpArchiveWriter.Rebuild(source));
        var entry = DdpArchive.Open(source).Index.Textures[0];
        var same = new Dictionary<int, byte[]> { [0] = source[(int)entry.Offset..(int)(entry.Offset + entry.Length)] };
        // The fixture's largest-entry word is synthetic; a texture rewrite recomputes it.
        var rebuilt = DdpArchiveWriter.Rebuild(source, textures: same);
        Assert.Equal(entry.Length, DdpArchive.Open(rebuilt).Index.TrailerUnknown);
    }

    [Fact]
    public void LargerTextureRelaysBlockAndUpdatesTrailer()
    {
        var source = DdpArchiveEditorTests.Archive();
        var before = DdpArchive.Open(source);
        var template = before.OpenMovie("first").Textures[0].Data;
        var pixels = Enumerable.Range(0, 5 * 3).SelectMany(i => new byte[] { (byte)(i * 10), 20, 30, 255 }).ToArray();
        var resized = NutTextureWriter.Write(template, 5, 3, pixels);
        var result = DdpArchiveWriter.Rebuild(source, textures: new Dictionary<int, byte[]> { [0] = resized });
        var after = DdpArchive.Open(result);
        var texture = NutFile.Parse(after.OpenMovie("first").Textures[0].Data).Textures[0];
        Assert.Equal((5, 3), ((int)texture.Width, (int)texture.Height));
        Assert.Equal(pixels, NutTextureDecoder.DecodeRgba8(texture));
        Assert.Equal(0u, after.Index.Textures[0].Length % 16);
        Assert.Equal(after.Index.Textures.Max(t => t.Length), after.Index.TrailerUnknown);
        Assert.Equal(before.Index.TrailerTextureCount, after.Index.TrailerTextureCount);
        Assert.Equal(before.OpenMovie("second").Data.ToArray(), after.OpenMovie("second").Data.ToArray());
        Assert.Equal(source[^3..], result[^3..]);
    }

    [Fact]
    public void NutWriterKeepsOpaqueHeaderBytesAndAlignsPayload()
    {
        var template = DdpArchiveEditorTests.Nut(2, 2, 0x11);
        template[0x10 + 0x30] = 0x65; // stand-in for the eXt block
        var written = NutTextureWriter.Write(template, 3, 3, new byte[3 * 3 * 4]);
        var texture = NutFile.Parse(written).Textures[0];
        Assert.Equal(48u, texture.DataSize); // 36 bytes rounded up to 16
        Assert.Equal(texture.HeaderSize + texture.DataSize, texture.TotalSize);
        Assert.Equal(0x65, written[0x10 + 0x30]);
        Assert.Equal(written.Length, 0x10 + texture.HeaderSize + (int)texture.DataSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => NutTextureWriter.Write(template, 4097, 1, new byte[4097 * 4]));
    }

    [Fact]
    public void Bc3EncoderRoundTripsFlatAndTwoColourBlocksExactly()
    {
        var pixels = new byte[8 * 4 * 4];
        for (var i = 0; i < 32; i++)
        {
            var left = i % 8 < 4;
            pixels[i * 4] = left ? (byte)255 : (byte)0; pixels[i * 4 + 1] = left ? (byte)255 : (byte)0;
            pixels[i * 4 + 2] = left ? (byte)255 : (byte)0; pixels[i * 4 + 3] = left ? (byte)255 : (byte)0;
        }
        var encoded = BcEncoder.Encode(pixels, 8, 4, NutPixelFormat.Bc3);
        Assert.Equal(pixels, BlockCompressionDecoder.DecodeBc3(encoded, 8, 4));
    }

    [Fact]
    public void TextureSizeAndStringEditsRoundTrip()
    {
        var document = new LmbEditDocument(LmbEditDocumentTests.Movie());
        Assert.Empty(document.Definition.TextureBounds);
        Assert.False(document.SetTextureSize(0, 64, 32));
        var index = (int)document.InternString("before");
        document.SetString(index, "a longer translated value");
        Assert.Equal("a longer translated value", document.Definition.Strings[index].Value);
        document.SetString(index, "x");
        Assert.Equal("x", document.Definition.Strings[index].Value);
    }
}
