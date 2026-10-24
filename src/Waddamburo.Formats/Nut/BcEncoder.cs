using System.Buffers.Binary;

namespace Waddamburo.Formats.Nut;

/// <summary>
/// Quality BC1/BC3 encoder: alpha-weighted principal-axis endpoints refined by least squares,
/// so text on transparent backgrounds keeps clean edges. Colour blocks always use the
/// four-colour mode (c0 &gt; c1) for BC3, which every decoder (including RSX) treats alike.
/// </summary>
public static class BcEncoder
{
    // Layout: https://learn.microsoft.com/windows/win32/direct3d10/d3d10-graphics-programming-guide-resources-block-compression
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, NutPixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height * 4 != rgba.Length) throw new ArgumentException("Invalid RGBA pixel count.", nameof(rgba));
        switch (format)
        {
            case NutPixelFormat.Argb or NutPixelFormat.ArgbAlternate:
                var argb = new byte[rgba.Length];
                for (var i = 0; i < rgba.Length; i += 4)
                { argb[i] = rgba[i + 3]; argb[i + 1] = rgba[i]; argb[i + 2] = rgba[i + 1]; argb[i + 3] = rgba[i + 2]; }
                return argb;
            case NutPixelFormat.Bc1 or NutPixelFormat.Bc3:
                break;
            default:
                throw new NotSupportedException($"Unsupported NTP3 format {format}.");
        }
        var bc3 = format == NutPixelFormat.Bc3;
        var blockBytes = bc3 ? 16 : 8;
        var columns = (width + 3) / 4; var rows = (height + 3) / 4;
        var output = new byte[columns * rows * blockBytes];
        var block = new byte[64];
        for (var by = 0; by < rows; by++)
        for (var bx = 0; bx < columns; bx++)
        {
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                rgba.Slice((Math.Min(by * 4 + y, height - 1) * width + Math.Min(bx * 4 + x, width - 1)) * 4, 4).CopyTo(block.AsSpan((y * 4 + x) * 4, 4));
            var destination = output.AsSpan((by * columns + bx) * blockBytes, blockBytes);
            if (bc3)
            {
                AlphaBlock(block, destination[..8]);
                ColorBlock(block, destination[8..], punchThrough: false);
            }
            else ColorBlock(block, destination, punchThrough: true);
        }
        return output;
    }

    /// <summary>Encodes one 4×4 R,G,B,A block's colours into 8 bytes.</summary>
    public static void ColorBlock(ReadOnlySpan<byte> block, Span<byte> output, bool punchThrough)
    {
        Span<float> r = stackalloc float[16]; Span<float> g = stackalloc float[16]; Span<float> b = stackalloc float[16];
        Span<float> w = stackalloc float[16];
        var transparent = false; float weightSum = 0;
        for (var i = 0; i < 16; i++)
        {
            r[i] = block[i * 4]; g[i] = block[i * 4 + 1]; b[i] = block[i * 4 + 2];
            var a = block[i * 4 + 3];
            if (punchThrough && a < 128) { transparent = true; w[i] = 0; continue; }
            // Colour under transparent texels is invisible: weight by coverage, never fully zero.
            w[i] = punchThrough ? 1 : 0.02f + a / 255f;
            weightSum += w[i];
        }
        if (weightSum <= 0)
        {
            // Entirely punched-through BC1 block.
            BinaryPrimitives.WriteUInt16LittleEndian(output, 0); BinaryPrimitives.WriteUInt16LittleEndian(output[2..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(output[4..], 0xFFFFFFFF);
            return;
        }

        // Weighted mean and covariance; principal axis by power iteration.
        float mr = 0, mg = 0, mb = 0;
        for (var i = 0; i < 16; i++) { mr += w[i] * r[i]; mg += w[i] * g[i]; mb += w[i] * b[i]; }
        mr /= weightSum; mg /= weightSum; mb /= weightSum;
        float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (var i = 0; i < 16; i++)
        {
            float dr = r[i] - mr, dg = g[i] - mg, db = b[i] - mb;
            crr += w[i] * dr * dr; crg += w[i] * dr * dg; crb += w[i] * dr * db;
            cgg += w[i] * dg * dg; cgb += w[i] * dg * db; cbb += w[i] * db * db;
        }
        float ar = 0.9f, ag = 1f, ab = 0.7f;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var nr = crr * ar + crg * ag + crb * ab;
            var ng = crg * ar + cgg * ag + cgb * ab;
            var nb = crb * ar + cgb * ag + cbb * ab;
            var length = MathF.Sqrt(nr * nr + ng * ng + nb * nb);
            if (length < 1e-6f) break;
            ar = nr / length; ag = ng / length; ab = nb / length;
        }
        float min = float.MaxValue, max = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            if (w[i] <= 0) continue;
            var t = (r[i] - mr) * ar + (g[i] - mg) * ag + (b[i] - mb) * ab;
            min = Math.Min(min, t); max = Math.Max(max, t);
        }
        var e0 = new Vector3(mr + ar * max, mg + ag * max, mb + ab * max);
        var e1 = new Vector3(mr + ar * min, mg + ag * min, mb + ab * min);

        var threeColor = punchThrough && transparent;
        Span<byte> bestIndices = stackalloc byte[16]; Span<byte> indices = stackalloc byte[16];
        ushort bestC0 = 0, bestC1 = 0; var bestError = float.MaxValue;
        for (var pass = 0; pass < 3; pass++)
        {
            var c0 = rgb565(e0); var c1 = rgb565(e1);
            var error = assign(r, g, b, w, block, c0, c1, threeColor, punchThrough, indices, out var normalizedC0, out var normalizedC1);
            if (error < bestError)
            {
                bestError = error; bestC0 = normalizedC0; bestC1 = normalizedC1; indices.CopyTo(bestIndices);
            }
            if (!refine(r, g, b, w, indices, normalizedC0, normalizedC1, threeColor, ref e0, ref e1)) break;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(output, bestC0);
        BinaryPrimitives.WriteUInt16LittleEndian(output[2..], bestC1);
        uint packed = 0;
        for (var i = 0; i < 16; i++) packed |= (uint)bestIndices[i] << (i * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], packed);
    }

    private static float assign(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> w,
        ReadOnlySpan<byte> block, ushort c0, ushort c1, bool threeColor, bool punchThrough, Span<byte> indices,
        out ushort outC0, out ushort outC1)
    {
        // Four-colour mode needs c0 > c1, three-colour mode c0 <= c1.
        if (threeColor ? c0 > c1 : c0 < c1) (c0, c1) = (c1, c0);
        if (!threeColor && c0 == c1)
        {
            // A flat block: index 0 decodes c0 in either mode.
            indices.Clear(); outC0 = c0; outC1 = c1;
            float flat = 0; var p = expand(c0);
            for (var i = 0; i < 16; i++) flat += w[i] * distance(r[i], g[i], b[i], p);
            return flat;
        }
        Span<Vector3> palette = stackalloc Vector3[4];
        palette[0] = expand(c0); palette[1] = expand(c1);
        if (threeColor)
        {
            palette[2] = (palette[0] + palette[1]) * 0.5f;
            palette[3] = default;
        }
        else
        {
            palette[2] = (palette[0] * 2 + palette[1]) / 3f;
            palette[3] = (palette[0] + palette[1] * 2) / 3f;
        }
        float total = 0;
        for (var i = 0; i < 16; i++)
        {
            if (punchThrough && block[i * 4 + 3] < 128) { indices[i] = 3; continue; }
            var best = 0; var bestDistance = float.MaxValue;
            for (var candidate = 0; candidate < (threeColor ? 3 : 4); candidate++)
            {
                var d = distance(r[i], g[i], b[i], palette[candidate]);
                if (d < bestDistance) { bestDistance = d; best = candidate; }
            }
            indices[i] = (byte)best;
            total += w[i] * bestDistance;
        }
        outC0 = c0; outC1 = c1;
        return total;
    }

    /// <summary>Least-squares endpoints for the current index assignment.</summary>
    private static bool refine(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> w,
        ReadOnlySpan<byte> indices, ushort c0, ushort c1, bool threeColor, ref Vector3 e0, ref Vector3 e1)
    {
        float aa = 0, bb = 0, ab = 0;
        Vector3 ax = default, bx = default;
        for (var i = 0; i < 16; i++)
        {
            if (w[i] <= 0) continue;
            float alpha = threeColor
                ? indices[i] switch { 0 => 1, 1 => 0, 2 => 0.5f, _ => -1 }
                : indices[i] switch { 0 => 1, 1 => 0, 2 => 2 / 3f, _ => 1 / 3f };
            if (alpha < 0) continue;
            var beta = 1 - alpha;
            var x = new Vector3(r[i], g[i], b[i]);
            aa += w[i] * alpha * alpha; bb += w[i] * beta * beta; ab += w[i] * alpha * beta;
            ax += x * (w[i] * alpha); bx += x * (w[i] * beta);
        }
        var determinant = aa * bb - ab * ab;
        if (MathF.Abs(determinant) < 1e-6f) return false;
        var n0 = (ax * bb - bx * ab) / determinant;
        var n1 = (bx * aa - ax * ab) / determinant;
        n0 = n0.Clamp(); n1 = n1.Clamp();
        if (rgb565(n0) == c0 && rgb565(n1) == c1 || rgb565(n0) == c1 && rgb565(n1) == c0) return false;
        e0 = n0; e1 = n1;
        return true;
    }

    /// <summary>Encodes one 4×4 block's alpha into 8 BC3 bytes, choosing the better of both alpha modes.</summary>
    public static void AlphaBlock(ReadOnlySpan<byte> block, Span<byte> output)
    {
        byte min = 255, max = 0, innerMin = 255, innerMax = 0;
        for (var i = 0; i < 16; i++)
        {
            var a = block[i * 4 + 3];
            min = Math.Min(min, a); max = Math.Max(max, a);
            if (a is not (0 or 255)) { innerMin = Math.Min(innerMin, a); innerMax = Math.Max(innerMax, a); }
        }
        Span<byte> eight = stackalloc byte[8]; Span<byte> six = stackalloc byte[8];
        var errorEight = alphaMode(block, max, min, eight);
        var errorSix = int.MaxValue;
        if (innerMin <= innerMax) errorSix = alphaMode(block, innerMin, innerMax, six);
        else if (min != max) errorSix = alphaMode(block, 0, 255, six);
        (errorSix < errorEight ? six : eight).CopyTo(output);
    }

    /// <summary>a0 &gt; a1 selects eight interpolated values; a0 &lt;= a1 six plus 0 and 255.</summary>
    private static int alphaMode(ReadOnlySpan<byte> block, byte a0, byte a1, Span<byte> output)
    {
        Span<int> palette = stackalloc int[8];
        palette[0] = a0; palette[1] = a1;
        if (a0 > a1)
            for (var i = 1; i <= 6; i++) palette[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        else
        {
            for (var i = 1; i <= 4; i++) palette[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
            palette[6] = 0; palette[7] = 255;
        }
        ulong indices = 0; var total = 0;
        for (var i = 0; i < 16; i++)
        {
            var value = block[i * 4 + 3];
            var best = 0; var bestError = int.MaxValue;
            for (var j = 0; j < 8; j++)
            {
                var error = Math.Abs(value - palette[j]);
                if (error < bestError) { best = j; bestError = error; }
            }
            indices |= (ulong)best << (i * 3);
            total += bestError * bestError;
        }
        output[0] = a0; output[1] = a1;
        for (var i = 0; i < 6; i++) output[i + 2] = (byte)(indices >> (i * 8));
        return total;
    }

    private static ushort rgb565(Vector3 c)
    {
        var red = (int)MathF.Round(Math.Clamp(c.X, 0, 255) * 31 / 255f);
        var green = (int)MathF.Round(Math.Clamp(c.Y, 0, 255) * 63 / 255f);
        var blue = (int)MathF.Round(Math.Clamp(c.Z, 0, 255) * 31 / 255f);
        return (ushort)((red << 11) | (green << 5) | blue);
    }

    private static Vector3 expand(ushort value)
    {
        var red = (value >> 11) & 31; var green = (value >> 5) & 63; var blue = value & 31;
        return new Vector3((red << 3) | (red >> 2), (green << 2) | (green >> 4), (blue << 3) | (blue >> 2));
    }

    private static float distance(float r, float g, float b, Vector3 p)
    {
        // Perceptual weights keep luminance (text strokes) accurate.
        float dr = r - p.X, dg = g - p.Y, db = b - p.Z;
        return 0.30f * dr * dr + 0.59f * dg * dg + 0.11f * db * db;
    }

    private readonly record struct Vector3(float X, float Y, float Z)
    {
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vector3 operator /(Vector3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);
        public Vector3 Clamp() => new(Math.Clamp(X, 0, 255), Math.Clamp(Y, 0, 255), Math.Clamp(Z, 0, 255));
    }
}
