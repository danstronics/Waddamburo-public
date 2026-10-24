using System.Buffers.Binary;

namespace Waddamburo.Formats.Nut;

/// <summary>Re-encodes replacement pixels into an existing allocation, retaining all headers and mip levels.</summary>
public static class NutPixelEditor
{
    // BC layouts: https://learn.microsoft.com/windows/win32/direct3d10/d3d10-graphics-programming-guide-resources-block-compression
    public static byte[] ReplacePixels(ReadOnlyMemory<byte> source, int textureIndex, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var pack = NutFile.Parse(source);
        var texture = pack.Textures[textureIndex];
        if (width != texture.Width || height != texture.Height)
            throw new InvalidOperationException($"Image must be exactly {texture.Width}×{texture.Height}; received {width}×{height}. Resizing texture allocations is disabled.");
        if ((long)width * height * 4 != rgba.Length) throw new ArgumentException("Invalid RGBA pixel count.", nameof(rgba));
        var output = source.ToArray();
        var pixels = rgba.ToArray();
        var offset = checked((int)texture.DataOffset);
        for (var mip = 0; mip < texture.MipCount; mip++)
        {
            var encoded = encode(pixels, width, height, texture.Format);
            encoded.CopyTo(output, offset);
            offset += encoded.Length;
            if (mip + 1 == texture.MipCount) break;
            var nextWidth = Math.Max(1, width / 2); var nextHeight = Math.Max(1, height / 2);
            var next = new byte[nextWidth * nextHeight * 4];
            for (var y = 0; y < nextHeight; y++)
            for (var x = 0; x < nextWidth; x++)
            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                    sum += pixels[(Math.Min(y * 2 + dy, height - 1) * width + Math.Min(x * 2 + dx, width - 1)) * 4 + channel];
                next[(y * nextWidth + x) * 4 + channel] = (byte)((sum + 2) / 4);
            }
            pixels = next; width = nextWidth; height = nextHeight;
        }
        _ = NutFile.Parse(output);
        return output;
    }

    private static byte[] encode(byte[] pixels, int width, int height, NutPixelFormat format)
    {
        if (format is NutPixelFormat.Argb or NutPixelFormat.ArgbAlternate)
        {
            var argb = new byte[pixels.Length];
            for (var i = 0; i < pixels.Length; i += 4)
            { argb[i] = pixels[i + 3]; argb[i + 1] = pixels[i]; argb[i + 2] = pixels[i + 1]; argb[i + 3] = pixels[i + 2]; }
            return argb;
        }
        var blockBytes = format == NutPixelFormat.Bc3 ? 16 : 8;
        var columns = (width + 3) / 4; var rows = (height + 3) / 4;
        var output = new byte[columns * rows * blockBytes];
        Span<byte> block = stackalloc byte[64];
        for (var by = 0; by < rows; by++)
        for (var bx = 0; bx < columns; bx++)
        {
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                pixels.AsSpan((Math.Min(by * 4 + y, height - 1) * width + Math.Min(bx * 4 + x, width - 1)) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4, 4));
            var destination = output.AsSpan((by * columns + bx) * blockBytes, blockBytes);
            if (format == NutPixelFormat.Bc3) alphaBlock(block, destination[..8]);
            colorBlock(block, destination[(blockBytes - 8)..], format == NutPixelFormat.Bc1);
        }
        return output;
    }

    private static void colorBlock(ReadOnlySpan<byte> block, Span<byte> output, bool allowTransparency)
    {
        var transparent = false;
        Span<int> minimum = stackalloc int[] { 255, 255, 255 };
        Span<int> maximum = stackalloc int[3];
        for (var i = 0; i < 16; i++)
        {
            if (block[i * 4 + 3] < 128 && allowTransparency) { transparent = true; continue; }
            for (var c = 0; c < 3; c++)
            { minimum[c] = Math.Min(minimum[c], block[i * 4 + c]); maximum[c] = Math.Max(maximum[c], block[i * 4 + c]); }
        }
        static ushort rgb565(ReadOnlySpan<int> c) => (ushort)(((c[0] * 31 + 127) / 255 << 11) | ((c[1] * 63 + 127) / 255 << 5) | (c[2] * 31 + 127) / 255);
        var hi = rgb565(maximum); var lo = rgb565(minimum);
        if (hi < lo) (hi, lo) = (lo, hi);
        if (!transparent && hi == lo) { if (hi < ushort.MaxValue) hi++; else lo--; }
        var c0 = transparent ? lo : hi; var c1 = transparent ? hi : lo;
        BinaryPrimitives.WriteUInt16LittleEndian(output, c0); BinaryPrimitives.WriteUInt16LittleEndian(output[2..], c1);
        Span<int> palette = stackalloc int[12];
        for (var endpoint = 0; endpoint < 2; endpoint++)
        {
            var value = endpoint == 0 ? c0 : c1;
            var r = (value >> 11) & 31; var g = (value >> 5) & 63; var b = value & 31;
            palette[endpoint * 3] = (r << 3) | (r >> 2); palette[endpoint * 3 + 1] = (g << 2) | (g >> 4); palette[endpoint * 3 + 2] = (b << 3) | (b >> 2);
        }
        for (var c = 0; c < 3; c++)
        {
            palette[6 + c] = transparent ? (palette[c] + palette[3 + c]) / 2 : (2 * palette[c] + palette[3 + c]) / 3;
            palette[9 + c] = transparent ? 0 : (palette[c] + 2 * palette[3 + c]) / 3;
        }
        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = transparent && block[i * 4 + 3] < 128 ? 3 : 0;
            if (best != 3)
            {
                var distance = int.MaxValue;
                for (var candidate = 0; candidate < (transparent ? 3 : 4); candidate++)
                {
                    var error = 0;
                    for (var c = 0; c < 3; c++) { var d = block[i * 4 + c] - palette[candidate * 3 + c]; error += d * d; }
                    if (error < distance) { distance = error; best = candidate; }
                }
            }
            indices |= (uint)best << (i * 2);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], indices);
    }

    private static void alphaBlock(ReadOnlySpan<byte> block, Span<byte> output)
    {
        byte minimum = 255; byte maximum = 0;
        for (var i = 0; i < 16; i++) { minimum = Math.Min(minimum, block[i * 4 + 3]); maximum = Math.Max(maximum, block[i * 4 + 3]); }
        output[0] = maximum; output[1] = minimum;
        Span<int> palette = stackalloc int[8]; palette[0] = maximum; palette[1] = minimum;
        if (maximum > minimum)
            for (var i = 1; i <= 6; i++) palette[i + 1] = ((7 - i) * maximum + i * minimum) / 7;
        else
        {
            for (var i = 1; i <= 4; i++) palette[i + 1] = ((5 - i) * maximum + i * minimum) / 5;
            palette[6] = 0; palette[7] = 255;
        }
        ulong indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = 0; var error = int.MaxValue;
            for (var j = 0; j < 8; j++) { var distance = Math.Abs(block[i * 4 + 3] - palette[j]); if (distance < error) { best = j; error = distance; } }
            indices |= (ulong)best << (i * 3);
        }
        for (var i = 0; i < 6; i++) output[i + 2] = (byte)(indices >> (i * 8));
    }

}
