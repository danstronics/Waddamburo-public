using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LumenStudio;

/// <summary>Zoomable pixel view of a texture with a floating layer and a rectangle tool.</summary>
internal sealed class TextureCanvas : Control
{
    private PointF _pan;
    private Point _panStart, _mouseStart;
    private bool _panning;
    private float _zoom = 1;
    private bool _fitted;

    public TextureCanvas()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(22, 24, 30);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Bitmap? Image { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Bitmap? Layer { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public PointF LayerPosition { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public float LayerScale { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public float LayerOpacity { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Rectangle? Marquee { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowPixelGrid { get; set; } = true;

    public float Zoom => _zoom;

    public event Action<PointF, MouseEventArgs>? ImageMouseDown;
    public event Action<PointF, MouseEventArgs>? ImageMouseMove;
    public event Action<PointF, MouseEventArgs>? ImageMouseUp;

    public RectangleF LayerBounds => Layer is null ? RectangleF.Empty
        : new RectangleF(LayerPosition.X, LayerPosition.Y, Layer.Width * LayerScale, Layer.Height * LayerScale);

    public void FitToWindow()
    {
        if (Image is null) return;
        _zoom = Math.Max(0.05f, Math.Min((ClientSize.Width - 40f) / Image.Width, (ClientSize.Height - 40f) / Image.Height));
        if (_zoom > 1) _zoom = MathF.Floor(_zoom);
        _pan = default; _fitted = true; Invalidate();
    }

    public void SetZoom(float zoom) { _zoom = Math.Clamp(zoom, 0.05f, 64); Invalidate(); }

    private PointF origin => Image is null ? default
        : new((ClientSize.Width - Image.Width * _zoom) / 2 + _pan.X, (ClientSize.Height - Image.Height * _zoom) / 2 + _pan.Y);

    public PointF ToImage(Point p) => new((p.X - origin.X) / _zoom, (p.Y - origin.Y) / _zoom);

    private RectangleF toScreen(RectangleF r) => new(origin.X + r.X * _zoom, origin.Y + r.Y * _zoom, r.Width * _zoom, r.Height * _zoom);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Image is null) return;
        if (!_fitted) FitToWindow();
        var area = toScreen(new RectangleF(0, 0, Image.Width, Image.Height));
        Imaging.Checkerboard(g, area, 10);
        g.InterpolationMode = _zoom >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(Image, area);
        if (Layer is not null)
        {
            var bounds = toScreen(LayerBounds);
            if (LayerOpacity < 1)
            {
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = LayerOpacity });
                g.DrawImage(Layer, Rectangle.Round(bounds), 0, 0, Layer.Width, Layer.Height, GraphicsUnit.Pixel, attributes);
            }
            else g.DrawImage(Layer, bounds);
            using var dash = new Pen(Color.FromArgb(220, 89, 220, 244)) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(dash, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        if (ShowPixelGrid && _zoom >= 10)
        {
            using var grid = new Pen(Color.FromArgb(40, 255, 255, 255));
            for (var x = 0; x <= Image.Width; x++) g.DrawLine(grid, area.X + x * _zoom, area.Y, area.X + x * _zoom, area.Bottom);
            for (var y = 0; y <= Image.Height; y++) g.DrawLine(grid, area.X, area.Y + y * _zoom, area.Right, area.Y + y * _zoom);
        }
        using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, area.X - 1, area.Y - 1, area.Width + 1, area.Height + 1);
        if (Marquee is { } marquee)
        {
            using var pen = new Pen(Theme.Highlight) { DashStyle = DashStyle.Dot };
            var r = toScreen(marquee);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.Button is MouseButtons.Middle or MouseButtons.Right) { _panning = true; _panStart = Point.Round(_pan); _mouseStart = e.Location; Capture = true; return; }
        ImageMouseDown?.Invoke(ToImage(e.Location), e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning) { _pan = new PointF(_panStart.X + e.X - _mouseStart.X, _panStart.Y + e.Y - _mouseStart.Y); Invalidate(); return; }
        ImageMouseMove?.Invoke(ToImage(e.Location), e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning) { _panning = false; Capture = false; return; }
        ImageMouseUp?.Invoke(ToImage(e.Location), e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var before = ToImage(e.Location);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25f : 0.8f), 0.05f, 64);
        var after = ToImage(e.Location);
        _pan = new PointF(_pan.X + (after.X - before.X) * _zoom, _pan.Y + (after.Y - before.Y) * _zoom);
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
}
