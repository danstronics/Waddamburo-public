using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LumenStudio.Core;

namespace LumenStudio;

/// <summary>Conversions between R,G,B,A buffers and GDI+ bitmaps.</summary>
internal static class Imaging
{
    public static Bitmap ToBitmap(RgbaImage image) => ToBitmap(image.Rgba, image.Width, image.Height);

    public static Bitmap ToBitmap(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3];
        }
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < height; row++) Marshal.Copy(bgra, row * width * 4, bits.Scan0 + row * bits.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(bits); }
        return bitmap;
    }

    public static RgbaImage FromBitmap(Bitmap source)
    {
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        }
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < bitmap.Height; row++) Marshal.Copy(bits.Scan0 + row * bits.Stride, pixels, row * bitmap.Width * 4, bitmap.Width * 4);
        }
        finally { bitmap.UnlockBits(bits); }
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return new RgbaImage(bitmap.Width, bitmap.Height, pixels);
    }

    /// <summary>Loads PNG (exact, including alpha) or any other GDI+ format.</summary>
    public static RgbaImage Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
        {
            try { return RgbaImage.FromPng(bytes); }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException) { }
        }
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        using var bitmap = new Bitmap(image);
        return FromBitmap(bitmap);
    }

    public static void SavePng(RgbaImage image, string path) => AtomicFile.Write(path, image.ToPng());

    public static void Checkerboard(Graphics graphics, RectangleF area, int cell = 8)
    {
        using var light = new SolidBrush(Color.FromArgb(58, 62, 72));
        using var dark = new SolidBrush(Color.FromArgb(44, 47, 56));
        graphics.FillRectangle(dark, area);
        var clip = graphics.Clip;
        graphics.SetClip(area);
        for (var y = area.Top; y < area.Bottom; y += cell)
            for (var x = area.Left + ((int)((y - area.Top) / cell) % 2) * cell; x < area.Right; x += cell * 2)
                graphics.FillRectangle(light, x, y, cell, cell);
        graphics.Clip = clip;
    }
}
