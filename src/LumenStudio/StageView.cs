using System.ComponentModel;
using System.Drawing.Drawing2D;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Rendering;

namespace LumenStudio;

/// <summary>
/// The stage: renders a Lumen snapshot through Waddamburo's bgfx compositor (CPU fallback otherwise),
/// draws hover/selection overlays and translates mouse input into stage coordinates.
/// </summary>
internal sealed class StageView : Control
{
    private EmbeddedRenderer? _gpu;
    private RenderTextureId _white;
    private RenderTextureId[] _textureIds = [];
    private LumenSoftwareTexture[] _uploaded = [];
    private LumenRenderSnapshot? _snapshot;
    private IReadOnlyList<LumenSoftwareTexture> _textures = [];
    private Bitmap? _software;
    private SdlDonRenderer? _don;
    private string? _donRoot;
    private static readonly LumenNativeSurfaceKey[] DonSurfaces = [new("studio:don:0"), new("studio:don:1")];

    private Point _mouseDown;
    private PointF _pan, _panStart;
    private bool _panning, _dragging, _resizing, _pressed;
    private RectangleF _dragStartBounds, _dragBounds;
    private float _zoom = 1;

    public StageView()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(25, 28, 35);
        TabStop = true;
        Dock = DockStyle.Fill;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public float StageWidth { get; set; } = 1280;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public float StageHeight { get; set; } = 720;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public RectangleF? SelectionBounds { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public RectangleF? HoverBounds { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowPlaceholders { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowGrid { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowStageBorder { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool CanTransform { get; set; } = true;
    public string RendererDescription { get; private set; } = "CPU";

    /// <summary>Left click on the stage (stage coordinates).</summary>
    public event Action<PointF, MouseEventArgs>? StageClicked;
    public event Action<PointF, MouseEventArgs>? StageDoubleClicked;
    public event Action<PointF?>? StageHover;
    /// <summary>Selection dragged by a stage delta.</summary>
    public event Action<float, float>? MoveCommitted;
    /// <summary>Selection box resized: horizontal and vertical factors about its top-left corner.</summary>
    public event Action<float, float, PointF>? ResizeCommitted;

    public void SetFrame(LumenRenderSnapshot snapshot, IReadOnlyList<LumenSoftwareTexture> textures)
    {
        (snapshot, textures) = TextSurfaces.Inline(snapshot, textures);
        _snapshot = snapshot;
        _textures = textures;
        if (_gpu is null)
        {
            var width = Math.Max(1, Math.Min(1280, (int)(StageWidth * scale)));
            var height = Math.Max(1, (int)(width * StageHeight / StageWidth));
            var pixels = LumenSoftwareRenderer.Render(new LumenRenderSnapshot(snapshot.StageWidth, snapshot.StageHeight, visibleQuads(snapshot)), textures, width, height);
            _software?.Dispose();
            _software = Imaging.ToBitmap(pixels, width, height);
        }
        else syncTextures();
        Invalidate();
    }

    public PointF ToStage(Point p) => new((p.X - origin.X) / scale, (p.Y - origin.Y) / scale);

    public void Fit() { _zoom = 1; _pan = default; Invalidate(); }

    public void ActualSize()
    {
        _zoom = 1 / Math.Max(0.0001f, fitScale);
        _pan = default; Invalidate();
    }

    public float ZoomPercent => scale * 100;

    public bool BindDon(LumenPlayer player, string gameRoot)
    {
        if (_gpu is null) return false;
        var path = Path.Combine(gameRoot, "data", "don3d");
        if (!File.Exists(Path.Combine(path, "ani", "don_bind.bin"))) return false;
        if (_don is null || _donRoot != path)
        {
            _don?.Dispose();
            _don = _gpu.CreateDonRenderer(path); _donRoot = path;
        }
        _don.SetCameraLayout(DonCameraLayout.Standard);
        for (var i = 0; i < 2; i++)
        {
            _don.SetMotion(i, null, "don_select_loop");
            player.SetNativeFill($"don{i + 1}pM", DonSurfaces[i], LumenNativeSurfacePlacement.Centered(600, 600));
        }
        return true;
    }

    public void AdvanceNative(double frames) => _don?.Advance(frames);

    public Bitmap? CaptureImage()
    {
        if (_gpu is null || _snapshot is null) return _software is null ? null : new Bitmap(_software);
        var capture = _gpu.Present(gpuFrame(), capture: true);
        return capture is null ? null : Imaging.ToBitmap(capture.Rgba8.AsSpan(), (int)capture.Width, (int)capture.Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            _gpu = new EmbeddedRenderer(Handle, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            _white = _gpu.Upload(1, 1, [255, 255, 255, 255]);
            RendererDescription = "GPU · " + _gpu.Driver;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.DoubleBuffer, false);
        }
        catch (Exception exception) when (exception is DllNotFoundException or InvalidOperationException or PlatformNotSupportedException or EntryPointNotFoundException)
        {
            _gpu?.Dispose(); _gpu = null;
            RendererDescription = "CPU preview (" + exception.Message + ")";
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _don?.Dispose(); _don = null; _donRoot = null;
        _gpu?.Dispose(); _gpu = null;
        _textureIds = []; _uploaded = [];
        base.OnHandleDestroyed(e);
    }

    private void syncTextures()
    {
        if (_gpu is null) return;
        if (_textureIds.Length != _textures.Count)
        {
            // The count changes with the dynamic text fields drawn (appended last): keep the movie's own
            // uploads and release only what no longer has an index.
            for (var i = _textures.Count; i < _textureIds.Length; i++)
                if (_uploaded[i] is not null) _gpu.Release(_textureIds[i]);
            Array.Resize(ref _textureIds, _textures.Count);
            Array.Resize(ref _uploaded, _textures.Count);
        }
        for (var i = 0; i < _textures.Count; i++)
        {
            if (ReferenceEquals(_uploaded[i], _textures[i])) continue;
            if (_uploaded[i] is not null) _gpu.Release(_textureIds[i]);
            var t = _textures[i];
            _textureIds[i] = _gpu.Upload((uint)t.Width, (uint)t.Height, t.Rgba.Span);
            _uploaded[i] = t;
        }
    }

    private static IEnumerable<LumenRenderQuad> visibleQuads(LumenRenderSnapshot snapshot) =>
        snapshot.Quads.Where(q => !(q.Origin is { Kind: not LumenQuadOriginKind.Texture } && q.MultiplyColor.Alpha <= 0 && q.NativeSurface is null));

    private float fitScale => Math.Max(0.0001f, Math.Min((ClientSize.Width - 40) / StageWidth, (ClientSize.Height - 40) / StageHeight));
    private float scale => fitScale * _zoom;
    private PointF origin => new((ClientSize.Width - StageWidth * scale) / 2 + _pan.X, (ClientSize.Height - StageHeight * scale) / 2 + _pan.Y);
    private RectangleF toScreen(RectangleF r) => new(origin.X + r.X * scale, origin.Y + r.Y * scale, r.Width * scale, r.Height * scale);

    private RenderFrame gpuFrame()
    {
        var quads = new List<RenderQuad>();
        var w = Math.Max(1, ClientSize.Width); var h = Math.Max(1, ClientSize.Height);
        void rectangle(RectangleF r, RenderColor color) => quads.Add(RenderQuad.FromRectangles(_white,
            new RenderRectangle(r.X / w, r.Y / h, r.Width / w, r.Height / h), RenderRectangle.Full, color, RenderColor.Transparent));
        void outline(RectangleF r, RenderColor color, float thickness)
        {
            rectangle(new RectangleF(r.X, r.Y, r.Width, thickness), color);
            rectangle(new RectangleF(r.X, r.Bottom - thickness, r.Width, thickness), color);
            rectangle(new RectangleF(r.X, r.Y, thickness, r.Height), color);
            rectangle(new RectangleF(r.Right - thickness, r.Y, thickness, r.Height), color);
        }
        var stage = toScreen(new RectangleF(0, 0, StageWidth, StageHeight));
        rectangle(stage, new RenderColor(.13f, .14f, .17f, 1));
        if (_snapshot is not null)
        {
            var frame = LumenRenderFrameAdapter.Compose(new LumenRenderSnapshot(_snapshot.StageWidth, _snapshot.StageHeight, visibleQuads(_snapshot)),
                RenderColor.Transparent,
                index => index < _textureIds.Length ? _textureIds[index] : _white,
                key => _don is not null && Array.IndexOf(DonSurfaces, key) is var slot && slot >= 0 ? _don.GetTexture(slot) : null);
            RenderVertex vertex(RenderVertex v) => v with { X = (stage.X + v.X * stage.Width) / w, Y = (stage.Y + v.Y * stage.Height) / h };
            foreach (var q in frame.Quads)
                quads.Add(q with { TopLeft = vertex(q.TopLeft), TopRight = vertex(q.TopRight), BottomRight = vertex(q.BottomRight), BottomLeft = vertex(q.BottomLeft) });
            if (ShowPlaceholders)
                foreach (var q in _snapshot.Quads)
                    if (q.Origin is { Kind: not LumenQuadOriginKind.Texture } && q.MultiplyColor.Alpha <= 0 && q.NativeSurface is null)
                        outline(toScreen(bounds(q)), q.Origin.Kind == LumenQuadOriginKind.Text ? new RenderColor(.95f, .7f, .3f, .55f) : new RenderColor(.6f, .5f, .95f, .45f), 1);
        }
        if (ShowGrid && scale * 32 > 10)
        {
            var color = new RenderColor(.65f, .75f, .85f, .10f);
            for (float x = 0; x <= StageWidth; x += 32) rectangle(new RectangleF(stage.X + x * scale, stage.Y, 1, stage.Height), color);
            for (float y = 0; y <= StageHeight; y += 32) rectangle(new RectangleF(stage.X, stage.Y + y * scale, stage.Width, 1), color);
        }
        if (ShowStageBorder) outline(stage, new RenderColor(.37f, .41f, .48f, 1), 1);
        if (HoverBounds is { } hover && !_dragging) outline(toScreen(hover), new RenderColor(.35f, .86f, .96f, .45f), 1);
        if ((_dragging || _resizing ? _dragBounds : SelectionBounds) is { } selected)
        {
            var r = toScreen(selected);
            var color = new RenderColor(.35f, .86f, .96f, 1);
            outline(r, color, 2);
            if (CanTransform) rectangle(new RectangleF(r.Right - 5, r.Bottom - 5, 10, 10), color);
        }
        return new RenderFrame(new RenderColor(.098f, .11f, .137f, 1), quads);
    }

    private static RectangleF bounds(LumenRenderQuad q)
    {
        var minX = Math.Min(Math.Min(q.TopLeft.X, q.TopRight.X), Math.Min(q.BottomRight.X, q.BottomLeft.X));
        var minY = Math.Min(Math.Min(q.TopLeft.Y, q.TopRight.Y), Math.Min(q.BottomRight.Y, q.BottomLeft.Y));
        var maxX = Math.Max(Math.Max(q.TopLeft.X, q.TopRight.X), Math.Max(q.BottomRight.X, q.BottomLeft.X));
        var maxY = Math.Max(Math.Max(q.TopLeft.Y, q.TopRight.Y), Math.Max(q.BottomRight.Y, q.BottomLeft.Y));
        return RectangleF.FromLTRB(minX, minY, maxX, maxY);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_gpu is not null)
        {
            _gpu.Resize(ClientSize.Width, ClientSize.Height);
            _gpu.Present(gpuFrame());
            return;
        }
        base.OnPaint(e);
        var g = e.Graphics;
        var stage = toScreen(new RectangleF(0, 0, StageWidth, StageHeight));
        Imaging.Checkerboard(g, stage, 12);
        if (_software is not null)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_software, stage);
        }
        using var border = new Pen(Color.FromArgb(95, 104, 123));
        g.DrawRectangle(border, stage.X, stage.Y, stage.Width, stage.Height);
        if (HoverBounds is { } hover && !_dragging)
        {
            using var pen = new Pen(Color.FromArgb(110, 89, 220, 244));
            var r = toScreen(hover); g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }
        if ((_dragging || _resizing ? _dragBounds : SelectionBounds) is { } selected)
        {
            using var pen = new Pen(Theme.Highlight, 2);
            var r = toScreen(selected); g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            if (CanTransform) { using var brush = new SolidBrush(Theme.Highlight); g.FillRectangle(brush, r.Right - 5, r.Bottom - 5, 10, 10); }
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _mouseDown = e.Location;
        if (e.Button is MouseButtons.Right or MouseButtons.Middle) { _panning = true; _panStart = _pan; Capture = true; return; }
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        if (CanTransform && SelectionBounds is { } current)
        {
            var r = toScreen(current);
            if (Math.Abs(e.X - r.Right) <= 8 && Math.Abs(e.Y - r.Bottom) <= 8)
            {
                _resizing = true; _dragStartBounds = _dragBounds = current; Capture = true; return;
            }
        }
        StageClicked?.Invoke(ToStage(e.Location), e);
        if (CanTransform && SelectionBounds is { } selection && selection.Contains(ToStage(e.Location)))
        {
            _dragStartBounds = _dragBounds = selection;
            Capture = true;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) StageDoubleClicked?.Invoke(ToStage(e.Location), e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning) { _pan = new PointF(_panStart.X + e.X - _mouseDown.X, _panStart.Y + e.Y - _mouseDown.Y); Invalidate(); return; }
        var dx = (e.X - _mouseDown.X) / scale; var dy = (e.Y - _mouseDown.Y) / scale;
        if (ModifierKeys.HasFlag(Keys.Shift) && !_resizing) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
        if (_resizing)
        {
            _dragBounds = new RectangleF(_dragStartBounds.X, _dragStartBounds.Y, Math.Max(1, _dragStartBounds.Width + dx), Math.Max(1, _dragStartBounds.Height + dy));
            if (ModifierKeys.HasFlag(Keys.Shift))
            {
                var factor = Math.Max(_dragBounds.Width / _dragStartBounds.Width, _dragBounds.Height / _dragStartBounds.Height);
                _dragBounds = new RectangleF(_dragStartBounds.X, _dragStartBounds.Y, _dragStartBounds.Width * factor, _dragStartBounds.Height * factor);
            }
            Invalidate(); return;
        }
        if (_pressed && Capture && !_dragging && (Math.Abs(e.X - _mouseDown.X) > 3 || Math.Abs(e.Y - _mouseDown.Y) > 3)) _dragging = true;
        if (_dragging) { _dragBounds = new RectangleF(_dragStartBounds.X + dx, _dragStartBounds.Y + dy, _dragStartBounds.Width, _dragStartBounds.Height); Invalidate(); return; }
        if (!_pressed) StageHover?.Invoke(ToStage(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        StageHover?.Invoke(null);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_resizing && _dragBounds != _dragStartBounds)
            ResizeCommitted?.Invoke(_dragBounds.Width / Math.Max(0.001f, _dragStartBounds.Width), _dragBounds.Height / Math.Max(0.001f, _dragStartBounds.Height), _dragStartBounds.Location);
        else if (_dragging && _dragBounds.Location != _dragStartBounds.Location)
            MoveCommitted?.Invoke(_dragBounds.X - _dragStartBounds.X, _dragBounds.Y - _dragStartBounds.Y);
        _dragging = _resizing = _panning = _pressed = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var before = ToStage(e.Location);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2f : 1 / 1.2f), 0.1f, 40);
        var after = ToStage(e.Location);
        _pan = new PointF(_pan.X + (after.X - before.X) * scale, _pan.Y + (after.Y - before.Y) * scale);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _software?.Dispose(); _don?.Dispose(); _don = null; _gpu?.Dispose(); _gpu = null; }
        base.Dispose(disposing);
    }
}
