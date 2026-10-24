using System.ComponentModel;
using Waddamburo.Formats.Lmb;

namespace LumenStudio;

/// <summary>One depth of a sprite timeline: presence spans and keyframes.</summary>
internal sealed class TimelineTrack
{
    public ushort Depth { get; init; }
    public string Title { get; set; } = "";
    public uint CharacterId { get; set; }
    public List<(int Start, int End)> Spans { get; } = [];
    public SortedSet<int> Keys { get; } = [];
    public HashSet<int> PlacedKeys { get; } = [];
    public bool IsMask { get; set; }
}

/// <summary>A sprite timeline in display form, built from its ordinary frame groups (not seek snapshots).</summary>
internal sealed class TimelineModel
{
    public int FrameCount { get; private init; }
    public List<TimelineTrack> Tracks { get; } = [];
    public List<(int Frame, string Label)> Labels { get; } = [];
    public List<(int Frame, uint Action)> Actions { get; } = [];

    public static TimelineModel Build(LmbMovieDefinition movie, LmbSpriteDefinition sprite)
    {
        var model = new TimelineModel { FrameCount = (int)Math.Max(1, sprite.DeclaredFrameCount) };
        var tracks = new Dictionary<ushort, TimelineTrack>();
        var open = new Dictionary<ushort, int>();
        string text(uint index) => index < movie.Strings.Length ? movie.Strings[(int)index].Value : "";
        int? frame = null;
        foreach (var command in sprite.Timeline)
        {
            switch (command)
            {
                case LmbShowFrameCommand show: frame = (int)show.Frame; break;
                case LmbFrameKeyCommand: frame = null; break;
                case LmbFrameLabelCommand label: model.Labels.Add(((int)label.Frame, text(label.StringIndex))); break;
                case LmbDoActionCommand action when frame is int at: model.Actions.Add((at, action.ActionIndex)); break;
                case LmbPlaceObjectCommand place when frame is int at:
                {
                    if (!tracks.TryGetValue(place.Depth, out var track)) tracks[place.Depth] = track = new TimelineTrack { Depth = place.Depth };
                    track.Keys.Add(at);
                    if (place.Mode is 1 or 3 || !open.ContainsKey(place.Depth))
                    {
                        if (open.Remove(place.Depth, out var start) && start < at) track.Spans.Add((start, at));
                        open[place.Depth] = at;
                        track.PlacedKeys.Add(at);
                        track.CharacterId = place.CharacterId;
                    }
                    if (place.NameStringIndex != 0 && text(place.NameStringIndex) is { Length: > 0 } name) track.Title = name;
                    if (place.ClipDepth != 0) track.IsMask = true;
                    break;
                }
                case LmbRemoveObjectCommand remove when frame is int at:
                    if (open.Remove(remove.Depth, out var begin) && tracks.TryGetValue(remove.Depth, out var removed)) removed.Spans.Add((begin, at));
                    break;
            }
        }
        foreach (var (depth, start) in open) tracks[depth].Spans.Add((start, model.FrameCount));
        foreach (var track in tracks.Values)
            if (track.Title.Length == 0) track.Title = $"#{track.CharacterId}";
        model.Tracks.AddRange(tracks.Values.OrderByDescending(t => t.Depth));
        return model;
    }
}

/// <summary>Frame ruler, label/script rows and one row per depth with spans and keyframes.</summary>
internal sealed class TimelineView : Control
{
    private int HeaderWidth => Theme.Scale(this, 210);
    private int RulerHeight => Theme.Scale(this, 22);
    private int LabelRow => Theme.Scale(this, 17);
    private int ActionRow => Theme.Scale(this, 16);
    private int RowHeight => Theme.Scale(this, 21);
    private readonly HScrollBar _horizontal = new() { Dock = DockStyle.Bottom };
    private readonly VScrollBar _vertical = new() { Dock = DockStyle.Right };
    private TimelineModel _model = new TimelineModelEmpty().Model;
    private float _frameWidth = 10;
    private bool _scrubbing;

    public TimelineView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        Font = Theme.UiFont;
        BackColor = Theme.Background;
        Controls.Add(_horizontal); Controls.Add(_vertical);
        _horizontal.Scroll += (_, _) => Invalidate();
        _vertical.Scroll += (_, _) => Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int CurrentFrame { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ushort? SelectedDepth { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Heading { get; set; } = "";
    public TimelineModel Model => _model;

    public event Action<int>? FrameRequested;
    public event Action<ushort, int>? TrackClicked;
    public event Action<ushort?, int, Point>? MenuRequested;

    public void SetModel(TimelineModel model)
    {
        _model = model;
        updateScroll();
        Invalidate();
    }

    public void SetFrame(int frame)
    {
        if (frame == CurrentFrame) return;
        CurrentFrame = frame;
        // Keep the playhead visible while playing.
        var x = frameX(frame);
        var right = ClientSize.Width - _vertical.Width;
        if (x < HeaderWidth || x > right - 20)
            _horizontal.Value = Math.Clamp((int)(frame * _frameWidth) - (right - HeaderWidth) / 3, 0, Math.Max(0, _horizontal.Maximum - _horizontal.LargeChange + 1));
        Invalidate();
    }

    private float frameX(int frame) => HeaderWidth + frame * _frameWidth - _horizontal.Value;
    private int topOfTracks => RulerHeight + LabelRow + ActionRow;

    private void updateScroll()
    {
        var visibleWidth = Math.Max(1, ClientSize.Width - HeaderWidth - _vertical.Width);
        var contentWidth = (int)(_model.FrameCount * _frameWidth) + 40;
        _horizontal.Maximum = Math.Max(0, contentWidth);
        _horizontal.LargeChange = visibleWidth;
        _horizontal.SmallChange = Math.Max(1, (int)_frameWidth);
        _horizontal.Value = Math.Clamp(_horizontal.Value, 0, Math.Max(0, _horizontal.Maximum - _horizontal.LargeChange + 1));
        _horizontal.Enabled = contentWidth > visibleWidth;
        var visibleHeight = Math.Max(1, ClientSize.Height - topOfTracks - _horizontal.Height);
        var contentHeight = _model.Tracks.Count * RowHeight + 8;
        _vertical.Maximum = Math.Max(0, contentHeight);
        _vertical.LargeChange = visibleHeight;
        _vertical.SmallChange = RowHeight;
        _vertical.Value = Math.Clamp(_vertical.Value, 0, Math.Max(0, _vertical.Maximum - _vertical.LargeChange + 1));
        _vertical.Enabled = contentHeight > visibleHeight;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); updateScroll(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        var width = ClientSize.Width - _vertical.Width;
        var height = ClientSize.Height - _horizontal.Height;
        using var surface = new SolidBrush(Theme.Surface);
        using var raised = new SolidBrush(Theme.SurfaceRaised);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Foreground);
        using var gridPen = new Pen(Color.FromArgb(40, 44, 54));
        using var majorPen = new Pen(Color.FromArgb(56, 61, 74));
        var first = Math.Max(0, (int)(_horizontal.Value / _frameWidth));
        var last = Math.Min(_model.FrameCount - 1, (int)((_horizontal.Value + width - HeaderWidth) / _frameWidth) + 1);
        var step = _frameWidth >= 8 ? 5 : _frameWidth >= 3 ? 10 : _frameWidth >= 1.2f ? 30 : 60;

        // Ruler, label and script rows.
        g.FillRectangle(raised, 0, 0, width, RulerHeight);
        g.FillRectangle(surface, 0, RulerHeight, width, LabelRow + ActionRow);
        g.SetClip(new Rectangle(HeaderWidth, 0, width - HeaderWidth, height));
        for (var frame = first - first % step; frame <= last; frame += step)
        {
            var x = frameX(frame);
            g.DrawLine(majorPen, x, RulerHeight - 7, x, height);
            g.DrawString(frame.ToString(System.Globalization.CultureInfo.InvariantCulture), Font, muted, x + 2, 3);
        }
        if (_frameWidth >= 4)
            for (var frame = first; frame <= last; frame++)
                if (frame % step != 0) { var x = frameX(frame); g.DrawLine(gridPen, x, RulerHeight - 4, x, RulerHeight); }
        using (var labelBrush = new SolidBrush(Color.FromArgb(240, 190, 90)))
            foreach (var (frame, label) in _model.Labels)
            {
                if (frame < first - 50 || frame > last) continue;
                var x = frameX(frame);
                g.FillPolygon(labelBrush, new[] { new PointF(x, RulerHeight + 2), new PointF(x + 7, RulerHeight + 6), new PointF(x, RulerHeight + 10) });
                g.DrawString(label, Font, labelBrush, x + 8, RulerHeight);
            }
        using (var actionBrush = new SolidBrush(Color.FromArgb(150, 130, 240)))
            foreach (var (frame, _) in _model.Actions)
            {
                if (frame < first || frame > last) continue;
                var x = frameX(frame);
                g.FillRectangle(actionBrush, x + 1, RulerHeight + LabelRow + 3, Math.Max(3, _frameWidth - 2), ActionRow - 6);
            }
        g.ResetClip();
        g.DrawString("Labels", Font, muted, 6, RulerHeight + 1);
        g.DrawString("Scripts", Font, muted, 6, RulerHeight + LabelRow);
        g.DrawString(Heading, Theme.BoldFont, text, 6, 3);

        // Tracks.
        var top = topOfTracks;
        g.SetClip(new Rectangle(0, top, width, height - top));
        using var spanBrush = new SolidBrush(Color.FromArgb(58, 88, 118));
        using var spanSelected = new SolidBrush(Color.FromArgb(70, 128, 168));
        using var maskBrush = new SolidBrush(Color.FromArgb(96, 70, 120));
        using var keyBrush = new SolidBrush(Color.FromArgb(225, 230, 240));
        using var keyHollow = new Pen(Color.FromArgb(225, 230, 240));
        using var selectedRow = new SolidBrush(Color.FromArgb(34, 52, 66));
        for (var row = 0; row < _model.Tracks.Count; row++)
        {
            var y = top + row * RowHeight - _vertical.Value;
            if (y + RowHeight < top || y > height) continue;
            var track = _model.Tracks[row];
            var selected = track.Depth == SelectedDepth;
            if (selected) g.FillRectangle(selectedRow, 0, y, width, RowHeight);
            g.DrawLine(gridPen, 0, y + RowHeight - 1, width, y + RowHeight - 1);
            g.DrawString($"{track.Depth,4}  {track.Title}", Font, selected ? text : muted, new RectangleF(4, y + 3, HeaderWidth - 8, RowHeight - 4));
            var clip = g.Clip;
            g.SetClip(new RectangleF(HeaderWidth, Math.Max(y, top), width - HeaderWidth, RowHeight));
            foreach (var (start, end) in track.Spans)
            {
                if (end < first || start > last + 1) continue;
                var x0 = frameX(start); var x1 = frameX(end);
                g.FillRectangle(track.IsMask ? maskBrush : selected ? spanSelected : spanBrush, x0, y + 3, Math.Max(2, x1 - x0 - 1), RowHeight - 7);
            }
            if (_frameWidth >= 2.5f)
                foreach (var key in track.Keys.GetViewBetween(Math.Max(0, first - 1), last + 1))
                {
                    var cx = frameX(key) + Math.Min(_frameWidth, 10) / 2; var cy = y + RowHeight / 2f - 1;
                    var r = Math.Clamp(_frameWidth / 2.6f, 2.5f, 4.5f);
                    var diamond = new[] { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) };
                    if (track.PlacedKeys.Contains(key)) g.FillPolygon(keyBrush, diamond); else g.DrawPolygon(keyHollow, diamond);
                }
            g.Clip = clip;
        }
        g.ResetClip();
        g.DrawLine(majorPen, HeaderWidth - 1, 0, HeaderWidth - 1, height);

        // Playhead.
        var playhead = frameX(CurrentFrame) + Math.Min(_frameWidth, 10) / 2;
        if (playhead >= HeaderWidth && playhead <= width)
        {
            using var pen = new Pen(Color.FromArgb(240, 90, 90), 1.5f);
            g.DrawLine(pen, playhead, 0, playhead, height);
            var caption = CurrentFrame.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var size = g.MeasureString(caption, Font);
            using var back = new SolidBrush(Color.FromArgb(240, 90, 90));
            g.FillRectangle(back, playhead - size.Width / 2 - 2, 2, size.Width + 4, 16);
            g.DrawString(caption, Font, Brushes.White, playhead - size.Width / 2, 3);
        }
        if (_model.Tracks.Count == 0)
            g.DrawString("This timeline places no elements.", Font, muted, HeaderWidth + 12, top + 10);
    }

    private int frameAt(int x) => Math.Clamp((int)((x - HeaderWidth + _horizontal.Value) / _frameWidth), 0, Math.Max(0, _model.FrameCount - 1));

    private TimelineTrack? trackAt(int y)
    {
        if (y < topOfTracks) return null;
        var row = (y - topOfTracks + _vertical.Value) / RowHeight;
        return row >= 0 && row < _model.Tracks.Count ? _model.Tracks[row] : null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var frame = frameAt(e.X);
        var track = trackAt(e.Y);
        if (e.Button == MouseButtons.Right)
        {
            if (track is not null) { SelectedDepth = track.Depth; TrackClicked?.Invoke(track.Depth, frame); }
            if (e.X >= HeaderWidth) FrameRequested?.Invoke(frame);
            MenuRequested?.Invoke(track?.Depth, frame, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (track is not null)
        {
            SelectedDepth = track.Depth;
            TrackClicked?.Invoke(track.Depth, frame);
        }
        if (e.X >= HeaderWidth)
        {
            _scrubbing = true;
            FrameRequested?.Invoke(frame);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_scrubbing && e.Button == MouseButtons.Left) FrameRequested?.Invoke(frameAt(e.X));
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _scrubbing = false; }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ModifierKeys.HasFlag(Keys.Control))
        {
            var anchor = frameAt(e.X);
            _frameWidth = Math.Clamp(_frameWidth * (e.Delta > 0 ? 1.25f : 0.8f), 0.25f, 48);
            updateScroll();
            _horizontal.Value = Math.Clamp((int)(anchor * _frameWidth) - (e.X - HeaderWidth), 0, Math.Max(0, _horizontal.Maximum - _horizontal.LargeChange + 1));
        }
        else if (ModifierKeys.HasFlag(Keys.Shift))
            _horizontal.Value = Math.Clamp(_horizontal.Value - e.Delta, 0, Math.Max(0, _horizontal.Maximum - _horizontal.LargeChange + 1));
        else
            _vertical.Value = Math.Clamp(_vertical.Value - e.Delta / 2, 0, Math.Max(0, _vertical.Maximum - _vertical.LargeChange + 1));
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    private sealed class TimelineModelEmpty
    {
        public TimelineModel Model { get; } = TimelineModel.Build(
            new LmbMovieDefinition(null!, null, [], [], [], [], [], [], [], [], [], []),
            new LmbSpriteDefinition(0, 0, 1, 0, 0, [], [], null!));
    }
}
