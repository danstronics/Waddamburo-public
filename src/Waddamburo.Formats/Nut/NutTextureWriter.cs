using System.Buffers.Binary;

namespace Waddamburo.Formats.Nut;

/// <summary>
/// Writes a single-texture NTP3 v1 file of any size from a template NUT, retaining the template's
/// file header and opaque texture-header bytes (eXt / GIDX blocks) and updating only the size,
/// format and dimension fields. Matches the observed Green layout: <c>dataSize = align16(payload)</c>,
/// <c>totalSize = headerSize + dataSize</c>.
/// </summary>
public static class NutTextureWriter
{
    /// <summary>RSX's largest 2D texture dimension.</summary>
    public const int MaxDimension = 4096;

    public static byte[] Write(ReadOnlyMemory<byte> template, int width, int height, ReadOnlySpan<byte> rgba, NutPixelFormat? format = null)
    {
        var pack = NutFile.Parse(template);
        if (pack.Version != 1 || pack.Textures.Length != 1)
            throw new NotSupportedException("Only single-texture NTP3 v1 files (the Green layout) can be rewritten.");
        var source = pack.Textures[0];
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), $"Texture dimensions must be 1–{MaxDimension}; received {width}×{height}.");
        if ((long)width * height * 4 != rgba.Length) throw new ArgumentException("Invalid RGBA pixel count.", nameof(rgba));
        var pixelFormat = format ?? source.Format;
        var mipCount = Math.Min((int)source.MipCount, maxMipCount(width, height));

        var levels = new List<byte[]>();
        var pixels = rgba.ToArray(); int levelWidth = width, levelHeight = height;
        for (var level = 0; level < mipCount; level++)
        {
            levels.Add(BcEncoder.Encode(pixels, levelWidth, levelHeight, pixelFormat));
            if (level + 1 == mipCount) break;
            (pixels, levelWidth, levelHeight) = halve(pixels, levelWidth, levelHeight);
        }
        var payloadLength = levels.Sum(static l => l.Length);
        var dataSize = (payloadLength + 15) & ~15;
        var headerSize = source.HeaderSize;
        var output = new byte[0x10 + headerSize + dataSize];
        pack.HeaderBytes.AsSpan().CopyTo(output);
        var header = output.AsSpan(0x10, headerSize);
        source.HeaderBytes.AsSpan().CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)(headerSize + dataSize)));
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], checked((uint)dataSize));
        header[0x11] = checked((byte)mipCount);
        header[0x13] = (byte)pixelFormat;
        BinaryPrimitives.WriteUInt16BigEndian(header[0x14..], (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(header[0x16..], (ushort)height);
        var offset = 0x10 + headerSize;
        foreach (var level in levels) { level.CopyTo(output, offset); offset += level.Length; }
        var check = NutFile.Parse(output).Textures[0];
        if (check.Width != width || check.Height != height || check.Format != pixelFormat)
            throw new InvalidDataException("NUT rewrite verification failed.");
        return output;
    }

    /// <summary>Size of the DDP entry the rewritten texture would occupy.</summary>
    public static long EntryLength(NutPixelFormat format, int width, int height, int headerSize = 0x50)
    {
        long raw = format switch
        {
            NutPixelFormat.Bc1 => (long)((width + 3) / 4) * ((height + 3) / 4) * 8,
            NutPixelFormat.Bc2 or NutPixelFormat.Bc3 => (long)((width + 3) / 4) * ((height + 3) / 4) * 16,
            _ => (long)width * height * 4,
        };
        return 0x10 + headerSize + ((raw + 15) & ~15);
    }

    private static int maxMipCount(int width, int height)
    {
        var count = 1;
        for (var size = Math.Max(width, height); size > 1; size /= 2) count++;
        return count;
    }

    private static (byte[] Pixels, int Width, int Height) halve(byte[] pixels, int width, int height)
    {
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
        return (next, nextWidth, nextHeight);
    }
}
