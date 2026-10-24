using System.Buffers.Binary;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Tests;

public sealed class NutPixelEditorTests
{
    [Theory]
    [InlineData(NutPixelFormat.Argb)]
    [InlineData(NutPixelFormat.ArgbAlternate)]
    [InlineData(NutPixelFormat.Bc1)]
    [InlineData(NutPixelFormat.Bc3)]
    public void ReencodesEveryMipWithoutChangingHeaderFormatOrAllocation(NutPixelFormat format)
    {
        var source = Pack(format, 8, 8, 4);
        var pixels = new byte[8 * 8 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 255; pixels[i + 3] = format == NutPixelFormat.Bc1 ? (byte)255 : (byte)128; }
        var output = NutPixelEditor.ReplacePixels(source, 0, 8, 8, pixels);
        Assert.Equal(source.Length, output.Length);
        Assert.Equal(source[..0x60], output[..0x60]);
        var texture = NutFile.Parse(output).Textures[0];
        Assert.Equal(format, texture.Format); Assert.Equal(4, texture.MipCount);
        Assert.Equal(pixels, NutTextureDecoder.DecodeRgba8(texture));
        Assert.NotEqual(source[^8..], output[^8..]);
    }

    [Fact]
    public void Bc1RetainsBinaryTransparencyAndBc3RetainsVaryingAlpha()
    {
        foreach (var format in new[] { NutPixelFormat.Bc1, NutPixelFormat.Bc3 })
        {
            var pixels = new byte[4 * 4 * 4];
            for (var i = 0; i < 16; i++) { pixels[i * 4] = 255; pixels[i * 4 + 3] = i % 2 == 0 ? (byte)0 : (byte)255; }
            var output = NutPixelEditor.ReplacePixels(Pack(format, 4, 4, 1), 0, 4, 4, pixels);
            var decoded = NutTextureDecoder.DecodeRgba8(NutFile.Parse(output).Textures[0]);
            for (var i = 0; i < 16; i++) Assert.Equal(pixels[i * 4 + 3], decoded[i * 4 + 3]);
        }
    }

    [Fact]
    public void OnePixelDifferenceIsRejectedBeforeMutatingOriginal()
    {
        var source = Pack(NutPixelFormat.Bc3, 8, 8, 1); var original = source.ToArray();
        Assert.Throws<InvalidOperationException>(() => NutPixelEditor.ReplacePixels(source, 0, 9, 8, new byte[9 * 8 * 4]));
        Assert.Equal(original, source);
    }

    private static byte[] Pack(NutPixelFormat format, ushort width, ushort height, byte mips)
    {
        var size = 0; int w = width; int h = height;
        for (var mip = 0; mip < mips; mip++)
        {
            size += format is NutPixelFormat.Argb or NutPixelFormat.ArgbAlternate ? w * h * 4 : ((w + 3) / 4) * ((h + 3) / 4) * (format == NutPixelFormat.Bc1 ? 8 : 16);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        var bytes = new byte[0x60 + size]; "NTP3"u8.CopyTo(bytes); bytes[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x10), (uint)(0x50 + size));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x18), (uint)size);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x1C), 0x50); bytes[0x21] = mips; bytes[0x23] = (byte)format;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x24), width); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x26), height);
        bytes.AsSpan(0x60).Fill(0xA5); return bytes;
    }
}
