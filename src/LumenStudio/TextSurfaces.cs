using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Waddamburo.Lumen.Rendering;

namespace LumenStudio;

/// <summary>
/// Dynamic text fields reach the renderer as native surfaces the game rasterizes. For the editor's stage
/// they are drawn here (the game's font when loaded) and turned into ordinary textured quads.
/// </summary>
internal static class TextSurfaces
{
    private static readonly Dictionary<LumenNativeSurfaceKey, LumenSoftwareTexture> s_cache = [];

    public static (LumenRenderSnapshot Snapshot, IReadOnlyList<LumenSoftwareTexture> Textures) Inline(
        LumenRenderSnapshot snapshot, IReadOnlyList<LumenSoftwareTexture> textures)
    {
        if (!snapshot.Quads.Any(q => q.NativeSurface is { } key && LumenTextSurface.TryParse(key, out _, out _, out _, out _, out _, out _)))
            return (snapshot, textures);
        var extended = textures.ToList();
        var quads = snapshot.Quads.Select(quad =>
        {
            if (quad.NativeSurface is not { } key || rasterize(key) is not { } texture) return quad;
            extended.Add(texture);
            return quad with { TextureIndex = (uint)(extended.Count - 1), NativeSurface = null };
        }).ToList();
        return (new LumenRenderSnapshot(snapshot.StageWidth, snapshot.StageHeight, quads), extended);
    }

    private static LumenSoftwareTexture? rasterize(LumenNativeSurfaceKey key)
    {
        if (s_cache.TryGetValue(key, out var cached)) return cached;
        if (!LumenTextSurface.TryParse(key, out var text, out var width, out var height, out var size, out var align, out var rgb)) return null;
        const int Oversample = 2;
        var w = Math.Clamp((int)Math.Ceiling(width * Oversample), 1, 4096);
        var h = Math.Clamp((int)Math.Ceiling(height * Oversample), 1, 4096);
        using var bitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var family = GameFonts.Resolve(GameFonts.Default ?? "Segoe UI");
            var style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            using var font = new Font(family, Math.Max(1, size * Oversample), style, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(255, (int)(rgb >> 16) & 0xFF, (int)(rgb >> 8) & 0xFF, (int)rgb & 0xFF));
            using var format = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = align switch { 'r' or 'R' => StringAlignment.Far, 'c' or 'C' => StringAlignment.Center, _ => StringAlignment.Near },
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
            };
            g.DrawString(text, font, brush, new RectangleF(0, 0, w, h), format);
        }
        var image = Imaging.FromBitmap(bitmap);
        var texture = new LumenSoftwareTexture(image.Width, image.Height, image.Rgba);
        if (s_cache.Count > 512) s_cache.Clear();
        s_cache[key] = texture;
        return texture;
    }
}
