using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LumenStudio;

/// <summary>Text appearance for translated texture labels: fill, two outlines (inner, outer) and a shadow.</summary>
internal sealed class TextStyle
{
    [Category("Text"), Description("The text; use new lines for several lines.")]
    public string Text { get; set; } = "Text";

    [Category("Font"), TypeConverter(typeof(FontFamilyConverter))]
    public string Font { get; set; } = defaultFont();

    [Category("Font"), Description("Size in texture pixels.")]
    public float Size { get; set; } = 32;

    [Category("Font")] public bool Bold { get; set; } = true;
    [Category("Font")] public bool Italic { get; set; }

    [Category("Layout"), Description("Extra space between letters, in pixels (negative tightens).")]
    public float LetterSpacing { get; set; }

    [Category("Layout"), Description("Line height as a multiple of the font size.")]
    public float LineSpacing { get; set; } = 1.1f;

    [Category("Layout")] public StringAlignment Alignment { get; set; } = StringAlignment.Center;

    [Category("Layout"), Description("Horizontal scale; below 1 condenses long translations into the same space.")]
    public float HorizontalScale { get; set; } = 1;

    [Category("Colours")] public Color Fill { get; set; } = Color.White;

    [Category("Colours"), Description("Inner outline width in pixels; 0 disables it.")]
    public float InnerOutline { get; set; }

    [Category("Colours")] public Color InnerOutlineColor { get; set; } = Color.White;

    [Category("Colours"), Description("Outer outline width in pixels, measured from the glyph edge; 0 disables it.")]
    public float OuterOutline { get; set; } = 4;

    [Category("Colours")] public Color OuterOutlineColor { get; set; } = Color.FromArgb(90, 50, 20);

    [Category("Shadow")] public float ShadowX { get; set; }
    [Category("Shadow")] public float ShadowY { get; set; }
    [Category("Shadow")] public Color ShadowColor { get; set; } = Color.FromArgb(160, 0, 0, 0);

    [Category("Quality"), Description("Anti-aliased edges (off gives hard pixel edges).")]
    public bool Smooth { get; set; } = true;

    public TextStyle Clone() => (TextStyle)MemberwiseClone();

    private static string defaultFont()
    {
        using var fonts = new InstalledFontCollection();
        foreach (var name in new[] { "Arial Rounded MT Bold", "Segoe UI Black", "Arial Black", "Segoe UI" })
            if (fonts.Families.Any(f => f.Name == name)) return name;
        return FontFamily.GenericSansSerif.Name;
    }

    /// <summary>Renders the text to a tightly cropped bitmap (with outline and shadow margins).</summary>
    public Bitmap Render()
    {
        using var family = new FontFamily(Font);
        var style = (Bold && family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular)
            | (Italic && family.IsStyleAvailable(FontStyle.Italic) ? FontStyle.Italic : FontStyle.Regular);
        if (!family.IsStyleAvailable(style)) style = FontStyle.Regular;
        var size = Math.Clamp(Size, 2, 1024);
        using var path = new GraphicsPath();
        var lines = (Text.Length == 0 ? " " : Text).Replace("\r\n", "\n").Split('\n');
        var lineHeight = size * Math.Max(0.3f, LineSpacing);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        var linePaths = new List<(GraphicsPath Path, float Width)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = new GraphicsPath();
            var x = 0f;
            foreach (var character in lines[i].EnumerateRunes())
            {
                var text = character.ToString();
                using var glyph = new GraphicsPath();
                glyph.AddString(text, family, (int)style, size, new PointF(0, 0), format);
                var advance = advanceOf(text, family, style, size, format);
                using var move = new Matrix();
                move.Translate(x, i * lineHeight);
                glyph.Transform(move);
                if (glyph.PointCount > 0) line.AddPath(glyph, false);
                x += advance + LetterSpacing;
            }
            linePaths.Add((line, Math.Max(0, x - LetterSpacing)));
        }
        var widest = linePaths.Max(l => l.Width);
        foreach (var (line, width) in linePaths)
        {
            var offset = Alignment switch { StringAlignment.Center => (widest - width) / 2, StringAlignment.Far => widest - width, _ => 0 };
            using var shift = new Matrix();
            shift.Translate(offset, 0);
            line.Transform(shift);
            if (line.PointCount > 0) path.AddPath(line, false);
            line.Dispose();
        }
        if (HorizontalScale is > 0 and not 1)
        {
            using var squeeze = new Matrix();
            squeeze.Scale(HorizontalScale, 1);
            path.Transform(squeeze);
        }
        var bounds = path.PointCount == 0 ? new RectangleF(0, 0, 1, 1) : path.GetBounds();
        var margin = Math.Max(InnerOutline, OuterOutline) + 2 + Math.Max(Math.Abs(ShadowX), Math.Abs(ShadowY));
        var widthPx = Math.Clamp((int)Math.Ceiling(bounds.Width + margin * 2), 1, 4096);
        var heightPx = Math.Clamp((int)Math.Ceiling(bounds.Height + margin * 2), 1, 4096);
        using (var origin = new Matrix())
        {
            origin.Translate(margin - bounds.X, margin - bounds.Y);
            path.Transform(origin);
        }
        var bitmap = new Bitmap(widthPx, heightPx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = Smooth ? SmoothingMode.AntiAlias : SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        void stroke(GraphicsPath shape, float width, Color color)
        {
            if (width <= 0 || color.A == 0) return;
            using var pen = new Pen(color, width * 2) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(pen, shape);
        }
        void fillAll(GraphicsPath shape, Color fill, bool silhouette)
        {
            if (silhouette)
            {
                // The shadow is the full silhouette, outlines included.
                stroke(shape, OuterOutline, fill); stroke(shape, InnerOutline, fill);
                using var shadowBrush = new SolidBrush(fill); g.FillPath(shadowBrush, shape);
                return;
            }
            stroke(shape, OuterOutline, OuterOutlineColor);
            stroke(shape, Math.Min(InnerOutline, OuterOutline > 0 ? OuterOutline : InnerOutline), InnerOutlineColor);
            using var brush = new SolidBrush(fill); g.FillPath(brush, shape);
        }
        if ((ShadowX != 0 || ShadowY != 0) && ShadowColor.A > 0)
        {
            using var shadow = (GraphicsPath)path.Clone();
            using var move = new Matrix();
            move.Translate(ShadowX, ShadowY);
            shadow.Transform(move);
            fillAll(shadow, ShadowColor, silhouette: true);
        }
        fillAll(path, Fill, silhouette: false);
        return bitmap;
    }

    private static float advanceOf(string text, FontFamily family, FontStyle style, float size, StringFormat format)
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        using var font = new System.Drawing.Font(family, size, style, GraphicsUnit.Pixel);
        if (text == " ") return g.MeasureString("a a", font, PointF.Empty, format).Width - g.MeasureString("aa", font, PointF.Empty, format).Width;
        return g.MeasureString(text, font, PointF.Empty, format).Width;
    }

    private sealed class FontFamilyConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
        {
            using var fonts = new InstalledFontCollection();
            return new StandardValuesCollection(fonts.Families.Select(f => f.Name).ToArray());
        }
    }
}
