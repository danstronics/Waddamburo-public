namespace LumenStudio.Core;

/// <summary>Image resampling for texture edits: premultiplied alpha, so transparent edges keep no colour fringes.</summary>
public static class ImageOps
{
    /// <summary>
    /// Separable Catmull-Rom resize; when shrinking, the kernel widens with the scale so every source
    /// texel contributes (no aliasing).
    /// </summary>
    public static byte[] Resize(ReadOnlySpan<byte> rgba, int width, int height, int outWidth, int outHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outHeight);
        if (outWidth == width && outHeight == height) return rgba.ToArray();
        var premultiplied = new float[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var a = rgba[i * 4 + 3] / 255f;
            premultiplied[i * 4] = rgba[i * 4] * a; premultiplied[i * 4 + 1] = rgba[i * 4 + 1] * a;
            premultiplied[i * 4 + 2] = rgba[i * 4 + 2] * a; premultiplied[i * 4 + 3] = rgba[i * 4 + 3];
        }
        var horizontal = new float[outWidth * height * 4];
        var columns = weights(width, outWidth);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < outWidth; x++)
            {
                var (start, kernel) = columns[x];
                float r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < kernel.Length; k++)
                {
                    var source = (y * width + Math.Clamp(start + k, 0, width - 1)) * 4;
                    var w = kernel[k];
                    r += premultiplied[source] * w; g += premultiplied[source + 1] * w; b += premultiplied[source + 2] * w; a += premultiplied[source + 3] * w;
                }
                var target = (y * outWidth + x) * 4;
                horizontal[target] = r; horizontal[target + 1] = g; horizontal[target + 2] = b; horizontal[target + 3] = a;
            }
        var output = new byte[outWidth * outHeight * 4];
        var rows = weights(height, outHeight);
        for (var y = 0; y < outHeight; y++)
        {
            var (start, kernel) = rows[y];
            for (var x = 0; x < outWidth; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < kernel.Length; k++)
                {
                    var source = (Math.Clamp(start + k, 0, height - 1) * outWidth + x) * 4;
                    var w = kernel[k];
                    r += horizontal[source] * w; g += horizontal[source + 1] * w; b += horizontal[source + 2] * w; a += horizontal[source + 3] * w;
                }
                var target = (y * outWidth + x) * 4;
                var alpha = Math.Clamp(a, 0, 255);
                output[target + 3] = (byte)MathF.Round(alpha);
                if (alpha < 0.5f) continue;
                var scale = 255f / alpha;
                output[target] = (byte)Math.Clamp(MathF.Round(r * scale), 0, 255);
                output[target + 1] = (byte)Math.Clamp(MathF.Round(g * scale), 0, 255);
                output[target + 2] = (byte)Math.Clamp(MathF.Round(b * scale), 0, 255);
            }
        }
        return output;
    }

    private static (int Start, float[] Kernel)[] weights(int source, int target)
    {
        var scale = (double)target / source;
        var support = scale < 1 ? 2 / scale : 2;
        var filterScale = scale < 1 ? scale : 1;
        var result = new (int, float[])[target];
        for (var i = 0; i < target; i++)
        {
            var center = (i + 0.5) / scale - 0.5;
            var start = (int)Math.Floor(center - support) + 1;
            var end = (int)Math.Floor(center + support);
            var kernel = new float[end - start + 1];
            double sum = 0;
            for (var k = 0; k < kernel.Length; k++)
            {
                var w = catmullRom((start + k - center) * filterScale);
                kernel[k] = (float)w; sum += w;
            }
            for (var k = 0; k < kernel.Length; k++) kernel[k] = (float)(kernel[k] / sum);
            result[i] = (start, kernel);
        }
        return result;
    }

    private static double catmullRom(double x)
    {
        x = Math.Abs(x);
        return x < 1 ? 1.5 * x * x * x - 2.5 * x * x + 1
            : x < 2 ? -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2 : 0;
    }

    /// <summary>Nearest-neighbour integer upscale: crisp previews and pixel-art textures.</summary>
    public static byte[] ResizeNearest(ReadOnlySpan<byte> rgba, int width, int height, int outWidth, int outHeight)
    {
        var output = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
                rgba.Slice(((y * height / outHeight) * width + x * width / outWidth) * 4, 4).CopyTo(output.AsSpan((y * outWidth + x) * 4, 4));
        return output;
    }
}
