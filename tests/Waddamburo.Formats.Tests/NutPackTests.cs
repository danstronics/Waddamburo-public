using System.Buffers.Binary;
using System.Text;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Tests;

public sealed class NutPackTests
{
    [Fact]
    public void V2PackExtractsAndSplicesAResizedTexture()
    {
        var pack = V2Pack((2, 2, 0x11), (4, 2, 0x22), (1, 1, 0x33));
        Assert.Equal(pack, NutPack.Rebuild(pack, new Dictionary<int, byte[]> { [1] = NutPack.Extract(pack, 1) }));
        var single = NutPack.Extract(pack, 1);
        var resized = NutTextureWriter.Write(single, 8, 4, Enumerable.Repeat((byte)0x7F, 8 * 4 * 4).ToArray());
        var rebuilt = NutPack.Rebuild(pack, new Dictionary<int, byte[]> { [1] = resized });
        var file = NutFile.Parse(rebuilt);
        Assert.Equal(2, file.Version);
        Assert.Equal((8, 4), ((int)file.Textures[1].Width, (int)file.Textures[1].Height));
        Assert.Equal(NutFile.Parse(pack).Textures[0].Data.ToArray(), file.Textures[0].Data.ToArray());
        Assert.Equal(NutFile.Parse(pack).Textures[2].Data.ToArray(), file.Textures[2].Data.ToArray());
    }

    [Fact]
    public void NdpRebuildRelocatesFollowingEntries()
    {
        var first = DdpArchiveEditorTests.Nut(2, 2, 0x11);
        var second = DdpArchiveEditorTests.Nut(1, 1, 0x22);
        var ndp = Ndp(("a.nut", first), ("b.nut", second));
        var archive = NdpArchive.Parse(ndp);
        Assert.Equal(["a.nut", "b.nut"], archive.Entries.Select(e => e.Name));
        Assert.Equal(ndp, NdpArchive.Rebuild(ndp, new Dictionary<int, byte[]> { [0] = first }));
        var bigger = NutTextureWriter.Write(first, 4, 4, new byte[4 * 4 * 4]);
        var rebuilt = NdpArchive.Rebuild(ndp, new Dictionary<int, byte[]> { [0] = bigger });
        var after = NdpArchive.Parse(rebuilt);
        Assert.Equal(bigger, after.Read(rebuilt, 0).ToArray());
        Assert.Equal(second, after.Read(rebuilt, 1).ToArray());
        Assert.Equal(0u, after.Entries[1].RelativeOffset % 16);
    }

    [Fact]
    public void Bc2DecodesExplicitAlpha()
    {
        var block = new byte[16];
        for (var i = 0; i < 8; i++) block[i] = 0xF0; // odd texels opaque, even transparent
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0xFFFF);
        var rgba = BlockCompressionDecoder.DecodeBc2(block, 4, 4);
        Assert.Equal(0, rgba[3]);
        Assert.Equal(255, rgba[7]);
        Assert.Equal(block[..8], BcEncoder.Encode(rgba, 4, 4, NutPixelFormat.Bc2)[..8]);
    }

    private static byte[] V2Pack(params (int Width, int Height, byte Fill)[] textures)
    {
        var headers = textures.Length * 0x50;
        using var stream = new MemoryStream();
        var file = new byte[0x10]; "NTP3"u8.CopyTo(file); file[4] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)textures.Length);
        stream.Write(file);
        long payload = 0x10 + headers;
        for (var i = 0; i < textures.Length; i++)
        {
            var (w, h, _) = textures[i];
            var size = w * h * 4;
            var header = new byte[0x50];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(0x50 + size));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)size);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x0C), 0x50);
            header[0x11] = 1; header[0x13] = 14;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x14), (ushort)w);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x16), (ushort)h);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x20), (uint)(payload - (0x10 + i * 0x50)));
            stream.Write(header);
            payload += size;
        }
        foreach (var (w, h, fill) in textures) stream.Write(Enumerable.Repeat(fill, w * h * 4).ToArray());
        return stream.ToArray();
    }

    private static byte[] Ndp(params (string Name, byte[] Nut)[] entries)
    {
        using var stream = new MemoryStream();
        void word(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
        stream.Write("NUT_PACK_TYPE1"u8); stream.Write(new byte[2]); word(2); stream.Write(new byte[2]); stream.Write(new byte[20]);
        word((uint)entries.Length); stream.Write(new byte[9]);
        uint offset = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var name = Encoding.UTF8.GetBytes(entries[i].Name);
            word((uint)name.Length); stream.Write(name);
            if (i == 0) stream.Write(new byte[5]);
            word(offset); word((uint)entries[i].Nut.Length);
            offset += (uint)entries[i].Nut.Length;
        }
        word(offset);
        foreach (var (_, nut) in entries) stream.Write(nut);
        return stream.ToArray();
    }
}
