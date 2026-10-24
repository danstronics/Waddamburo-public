using System.Diagnostics;
using System.Drawing.Drawing2D;
using LumenStudio.Core;

namespace LumenStudio;

/// <summary>
/// Pixel editor for one texture: text layers in the game's outlined style, image layers, erase/fill,
/// eyedropper, scaling to any size, flips and round-trips through an external editor.
/// </summary>
internal sealed class TextureEditorForm : Form
{
    private enum Tool { Move, Text, Erase, Fill, Eyedropper }

    private readonly TextureCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly PropertyGrid _properties = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true, PropertySort = PropertySort.Categorized };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly CheckBox _propagate = new() { AutoSize = true, Checked = true };
    private readonly Panel _colorSwatch = new() { Width = 28, Height = 22, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4) };
    private readonly List<Bitmap> _undo = [];
    private readonly Stack<Bitmap> _redo = new();
    private readonly Dictionary<Tool, ToolStripButton> _toolButtons = [];
    private readonly Size _originalSize;
    private readonly string _name;
    private Bitmap _image;
    private Tool _tool = Tool.Move;
    private TextStyle? _text;
    private Bitmap? _layerSource;
    private PointF _dragStart, _layerStart;
    private bool _draggingLayer, _marquee;
    private Color _color = Color.White;
    private FileSystemWatcher? _watcher;
    private string? _externalPath;
    private DateTime _externalStamp;
    private static TextStyle s_lastStyle = new();

    public TextureEditorForm(RgbaImage image, string name, int cloneCount, string? cloneNote = null)
    {
        Theme.BeginScaling(this);
        _image = Imaging.ToBitmap(image);
        _originalSize = new Size(image.Width, image.Height);
        _name = name;
        Text = $"Texture Editor · {name}";
        Width = 1280; Height = 860; MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Padding = new Padding(4, 2, 4, 2) };
        void button(string text, Action action, string tip) { var b = new ToolStripButton(text) { ToolTipText = tip }; b.Click += (_, _) => Ui.Guard(this, action); tools.Items.Add(b); }
        void tool(Tool kind, string text, string tip)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, CheckOnClick = false };
            b.Click += (_, _) => setTool(kind);
            _toolButtons[kind] = b; tools.Items.Add(b);
        }
        button("↶ Undo", undo, "Undo (Ctrl+Z)");
        button("↷ Redo", redo, "Redo (Ctrl+Y)");
        tools.Items.Add(new ToolStripSeparator());
        tool(Tool.Move, "✥ Move layer", "Drag the floating text or image layer");
        tool(Tool.Text, "T Text", "Click on the texture to place a new text layer (T)");
        tool(Tool.Erase, "⌫ Erase", "Drag a rectangle to make it fully transparent (E)");
        tool(Tool.Fill, "▇ Fill", "Drag a rectangle to fill it with the current colour (F)");
        tool(Tool.Eyedropper, "⊙ Pick colour", "Click to take the colour under the cursor (I)");
        tools.Items.Add(new ToolStripSeparator());
        button("Colour…", chooseColor, "Current fill colour for Fill and new text");
        button("Image layer…", importLayer, "Place an image over the texture as a movable layer");
        button("Commit layer", commitLayer, "Merge the floating layer into the texture (Enter)");
        button("Discard layer", discardLayer, "Remove the floating layer (Esc)");
        tools.Items.Add(new ToolStripSeparator());
        button("Size…", resizeDialog, "Change the texture resolution (scale) or its canvas size");
        button("2× upscale", () => scale(2), "Double the resolution; the element keeps its on-screen size");
        button("Flip H", () => transform(RotateFlipType.RotateNoneFlipX), "Mirror horizontally");
        button("Flip V", () => transform(RotateFlipType.RotateNoneFlipY), "Mirror vertically");
        button("Clear all", clearAll, "Make the whole texture transparent");
        tools.Items.Add(new ToolStripSeparator());
        button("Edit externally…", editExternally, "Open in Photoshop/GIMP/Paint.NET; changes are reloaded automatically when you save there");
        button("Import PNG…", importWhole, "Replace the whole texture with an image file (any size)");
        button("Export PNG…", exportPng, "Save the current texture as PNG");
        tools.Items.Add(new ToolStripSeparator());
        button("Fit", _canvas.FitToWindow, "Fit to window");
        button("1:1", () => _canvas.SetZoom(1), "Actual pixels");

        var side = new Panel { Dock = DockStyle.Right, Width = 330, Padding = new Padding(6) };
        var colorRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        colorRow.Controls.Add(new Label { Text = "Current colour", AutoSize = true, Margin = new Padding(4, 8, 4, 4) });
        colorRow.Controls.Add(_colorSwatch);
        _colorSwatch.Click += (_, _) => chooseColor();
        side.Controls.Add(_properties);
        side.Controls.Add(colorRow);
        side.Controls.Add(Ui.Caption("Layer / tool settings"));

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 44, ColumnCount = 4, Padding = new Padding(6, 4, 6, 4) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _propagate.Text = cloneCount > 0 ? $"Also apply to {cloneCount} linked clone{(cloneCount == 1 ? "" : "s")}{cloneNote}" : "No linked clones";
        _propagate.Enabled = cloneCount > 0; _propagate.Checked = cloneCount > 0;
        var apply = new Button { Text = "Apply", Width = 110, Height = 30, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        apply.Click += (_, _) => { if (_layerSource is not null) commitLayer(); };
        bottom.Controls.Add(_status, 0, 0); bottom.Controls.Add(_propagate, 1, 0); bottom.Controls.Add(cancel, 2, 0); bottom.Controls.Add(apply, 3, 0);
        CancelButton = cancel;

        Controls.Add(_canvas); Controls.Add(side); Controls.Add(tools); Controls.Add(bottom);
        Theme.Apply(this);
        _canvas.Image = _image;
        _colorSwatch.BackColor = _color;
        _canvas.ImageMouseDown += mouseDown;
        _canvas.ImageMouseMove += mouseMove;
        _canvas.ImageMouseUp += mouseUp;
        _properties.PropertyValueChanged += (_, _) => Ui.Guard(this, renderText);
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK && _undo.Count > 0 && !Ui.Confirm(this, "Discard the changes to this texture?")) e.Cancel = true;
        };
        FormClosed += (_, _) => { _watcher?.Dispose(); };
        setTool(Tool.Move);
        updateStatus(null);
    }

    /// <summary>Test hook: places a text layer with the remembered style, as a click with the Text tool would.</summary>
    internal void PlaceText(string text, float x, float y)
    {
        setTool(Tool.Text);
        mouseDown(new PointF(x, y), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
        _text!.Text = text;
        renderText();
        _canvas.LayerPosition = new PointF(MathF.Round(x - _layerSource!.Width / 2f), MathF.Round(y - _layerSource.Height / 2f));
    }

    internal void Commit() => commitLayer();

    internal TextStyle? TextLayer => _text;

    public RgbaImage Result => Imaging.FromBitmap(_image);

    public bool Propagate => _propagate.Checked;

    private void setTool(Tool tool)
    {
        _tool = tool;
        foreach (var (kind, button) in _toolButtons) button.Checked = kind == tool;
        _canvas.Cursor = tool switch { Tool.Text => Cursors.IBeam, Tool.Erase or Tool.Fill or Tool.Eyedropper => Cursors.Cross, _ => Cursors.Default };
        updateStatus(null);
    }

    private void mouseDown(PointF point, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        switch (_tool)
        {
            case Tool.Text:
                if (_layerSource is not null && _canvas.LayerBounds.Contains(point)) { startLayerDrag(point); return; }
                if (_layerSource is not null) commitLayer();
                _text = s_lastStyle.Clone();
                if (_text.Size > _image.Height) _text.Size = Math.Max(8, _image.Height * 0.6f);
                _properties.SelectedObject = _text;
                renderText();
                if (_layerSource is not null)
                    _canvas.LayerPosition = new PointF(point.X - _layerSource.Width / 2f, point.Y - _layerSource.Height / 2f);
                setTool(Tool.Move);
                _canvas.Invalidate();
                break;
            case Tool.Move:
                if (_layerSource is not null) startLayerDrag(point);
                break;
            case Tool.Erase or Tool.Fill:
                _marquee = true; _dragStart = point;
                _canvas.Marquee = new Rectangle(Point.Truncate(point), Size.Empty);
                break;
            case Tool.Eyedropper:
                var x = (int)point.X; var y = (int)point.Y;
                if (x >= 0 && y >= 0 && x < _image.Width && y < _image.Height) { _color = _image.GetPixel(x, y); _colorSwatch.BackColor = Color.FromArgb(255, _color); }
                setTool(Tool.Move);
                break;
        }
    }

    private void startLayerDrag(PointF point)
    {
        _draggingLayer = true; _dragStart = point; _layerStart = _canvas.LayerPosition;
    }

    private void mouseMove(PointF point, MouseEventArgs e)
    {
        if (_draggingLayer)
        {
            var dx = point.X - _dragStart.X; var dy = point.Y - _dragStart.Y;
            _canvas.LayerPosition = new PointF(MathF.Round(_layerStart.X + dx), MathF.Round(_layerStart.Y + dy));
            _canvas.Invalidate();
        }
        else if (_marquee)
        {
            var r = RectangleF.FromLTRB(Math.Min(_dragStart.X, point.X), Math.Min(_dragStart.Y, point.Y), Math.Max(_dragStart.X, point.X), Math.Max(_dragStart.Y, point.Y));
            _canvas.Marquee = Rectangle.Intersect(Rectangle.FromLTRB((int)MathF.Floor(r.Left), (int)MathF.Floor(r.Top), (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom)),
                new Rectangle(0, 0, _image.Width, _image.Height));
            _canvas.Invalidate();
        }
        updateStatus(point);
    }

    private void mouseUp(PointF point, MouseEventArgs e)
    {
        _draggingLayer = false;
        if (!_marquee) return;
        _marquee = false;
        if (_canvas.Marquee is { Width: > 0, Height: > 0 } area)
        {
            checkpoint();
            using var g = Graphics.FromImage(_image);
            g.CompositingMode = CompositingMode.SourceCopy;
            using var brush = new SolidBrush(_tool == Tool.Erase ? Color.Transparent : _color);
            g.FillRectangle(brush, area);
        }
        _canvas.Marquee = null;
        _canvas.Invalidate();
    }

    private void renderText()
    {
        if (_text is null) return;
        s_lastStyle = _text.Clone();
        var bitmap = _text.Render();
        var position = _canvas.LayerPosition;
        if (_layerSource is not null) position = new PointF(position.X + (_layerSource.Width - bitmap.Width) / 2f, position.Y + (_layerSource.Height - bitmap.Height) / 2f);
        else position = new PointF((_image.Width - bitmap.Width) / 2f, (_image.Height - bitmap.Height) / 2f);
        _layerSource?.Dispose();
        _layerSource = bitmap;
        _canvas.Layer = bitmap; _canvas.LayerScale = 1; _canvas.LayerPosition = new PointF(MathF.Round(position.X), MathF.Round(position.Y));
        _canvas.Invalidate();
        updateStatus(null);
    }

    private void importLayer()
    {
        using var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tga;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_layerSource is not null) commitLayer();
        var image = Imaging.Load(dialog.FileName);
        _text = null;
        _layerSource = Imaging.ToBitmap(image);
        var settings = new LayerSettings(this);
        _properties.SelectedObject = settings;
        _canvas.Layer = _layerSource;
        _canvas.LayerScale = Math.Min(1, Math.Min((float)_image.Width / image.Width, (float)_image.Height / image.Height));
        _canvas.LayerPosition = new PointF((_image.Width - image.Width * _canvas.LayerScale) / 2, (_image.Height - image.Height * _canvas.LayerScale) / 2);
        setTool(Tool.Move);
        _canvas.Invalidate();
    }

    private void commitLayer()
    {
        if (_layerSource is null) return;
        checkpoint();
        using (var g = Graphics.FromImage(_image))
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var bounds = _canvas.LayerBounds;
            if (_canvas.LayerOpacity < 1)
            {
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = _canvas.LayerOpacity });
                g.DrawImage(_layerSource, Rectangle.Round(bounds), 0, 0, _layerSource.Width, _layerSource.Height, GraphicsUnit.Pixel, attributes);
            }
            else g.DrawImage(_layerSource, bounds);
        }
        discardLayer();
    }

    private void discardLayer()
    {
        _layerSource?.Dispose(); _layerSource = null;
        _canvas.Layer = null; _canvas.LayerOpacity = 1; _canvas.LayerScale = 1;
        _text = null; _properties.SelectedObject = null;
        _canvas.Invalidate();
    }

    private void chooseColor()
    {
        using var dialog = new ColorDialog { Color = _color, FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { _color = dialog.Color; _colorSwatch.BackColor = _color; }
    }

    private void resizeDialog()
    {
        using var dialog = new SizeDialog(_image.Size, _originalSize);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        checkpoint();
        var current = Imaging.FromBitmap(_image);
        RgbaImage next;
        if (dialog.ScaleImage) next = current.Resize(dialog.NewSize.Width, dialog.NewSize.Height);
        else
        {
            using var canvas = new Bitmap(dialog.NewSize.Width, dialog.NewSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                var x = dialog.Centered ? (dialog.NewSize.Width - _image.Width) / 2 : 0;
                var y = dialog.Centered ? (dialog.NewSize.Height - _image.Height) / 2 : 0;
                g.DrawImageUnscaled(_image, x, y);
            }
            next = Imaging.FromBitmap(canvas);
        }
        replaceImage(Imaging.ToBitmap(next));
    }

    private void scale(int factor)
    {
        var w = _image.Width * factor; var h = _image.Height * factor;
        if (w > Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension || h > Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension)
            throw new InvalidOperationException($"{w}×{h} exceeds the {Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension}-pixel hardware limit.");
        checkpoint();
        replaceImage(Imaging.ToBitmap(Imaging.FromBitmap(_image).Resize(w, h)));
        if (_text is not null) { _text.Size *= factor; _canvas.LayerPosition = new PointF(_canvas.LayerPosition.X * factor, _canvas.LayerPosition.Y * factor); renderText(); }
    }

    private void transform(RotateFlipType flip)
    {
        checkpoint();
        _image.RotateFlip(flip);
        _canvas.Invalidate();
    }

    private void clearAll()
    {
        checkpoint();
        using var g = Graphics.FromImage(_image);
        g.Clear(Color.Transparent);
        _canvas.Invalidate();
    }

    private void importWhole()
    {
        using var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tga;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        loadFile(dialog.FileName);
    }

    private void loadFile(string path)
    {
        var image = Imaging.Load(path);
        if (image.Width > Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension || image.Height > Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension)
            throw new InvalidOperationException($"Images are limited to {Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension} pixels per side.");
        checkpoint();
        replaceImage(Imaging.ToBitmap(image));
    }

    private void exportPng()
    {
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = Path.GetFileNameWithoutExtension(_name.Replace('/', '_')) + ".png" };
        if (dialog.ShowDialog(this) == DialogResult.OK) Imaging.SavePng(Imaging.FromBitmap(_image), dialog.FileName);
    }

    private void editExternally()
    {
        if (_layerSource is not null) commitLayer();
        var folder = Path.Combine(Path.GetTempPath(), "LumenStudio", "external");
        Directory.CreateDirectory(folder);
        var stem = string.Concat(Path.GetFileNameWithoutExtension(_name.Replace('/', '_')).Where(c => char.IsLetterOrDigit(c) || c is '_' or '-'));
        _externalPath = Path.Combine(folder, $"{(stem.Length > 40 ? stem[..40] : stem)}_{Guid.NewGuid():N}"[..Math.Min(49, stem.Length + 33)] + ".png");
        Imaging.SavePng(Imaging.FromBitmap(_image), _externalPath);
        _externalStamp = File.GetLastWriteTimeUtc(_externalPath);
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(folder, Path.GetFileName(_externalPath)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
        void changed(object? _, FileSystemEventArgs __) => BeginInvoke(reloadExternal);
        _watcher.Changed += changed; _watcher.Created += changed; _watcher.Renamed += (_, _) => BeginInvoke(reloadExternal);
        Process.Start(new ProcessStartInfo(_externalPath) { UseShellExecute = true, Verb = "edit" })?.Dispose();
        _status.Text = "Editing externally: save the PNG in your editor and it is reloaded here automatically.";
    }

    private async void reloadExternal()
    {
        if (_externalPath is null || !File.Exists(_externalPath)) return;
        var stamp = File.GetLastWriteTimeUtc(_externalPath);
        if (stamp == _externalStamp) return;
        _externalStamp = stamp;
        // Editors often write in several steps; read once the file is complete.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { loadFile(_externalPath); _status.Text = $"Reloaded from the external editor at {DateTime.Now:HH:mm:ss}."; return; }
            catch (IOException) { await Task.Delay(150); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException) { await Task.Delay(150); }
        }
    }

    private void replaceImage(Bitmap next)
    {
        var old = _image;
        _image = next;
        _canvas.Image = _image;
        _canvas.FitToWindow();
        if (!_undo.Contains(old) && !_redo.Contains(old)) old.Dispose();
        updateStatus(null);
    }

    private void checkpoint()
    {
        _undo.Add(new Bitmap(_image));
        while (_undo.Count > 40) { _undo[0].Dispose(); _undo.RemoveAt(0); }
        while (_redo.Count > 0) _redo.Pop().Dispose();
    }

    private void undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_image);
        _image = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _canvas.Image = _image; _canvas.Invalidate(); updateStatus(null);
    }

    private void redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_image);
        _image = _redo.Pop();
        _canvas.Image = _image; _canvas.Invalidate(); updateStatus(null);
    }

    private void updateStatus(PointF? point)
    {
        var size = _image.Size == _originalSize ? $"{_image.Width}×{_image.Height}" : $"{_image.Width}×{_image.Height} (original {_originalSize.Width}×{_originalSize.Height}, keeps its on-screen size)";
        var position = point is { } p && p.X >= 0 && p.Y >= 0 && p.X < _image.Width && p.Y < _image.Height
            ? $" · {(int)p.X},{(int)p.Y} · {colorText(_image.GetPixel((int)p.X, (int)p.Y))}" : "";
        var hint = _tool switch
        {
            Tool.Text => " · Click to place a text layer",
            Tool.Erase => " · Drag to erase",
            Tool.Fill => " · Drag to fill",
            Tool.Eyedropper => " · Click to pick a colour",
            _ when _layerSource is not null => " · Drag the layer; Enter commits it",
            _ => "",
        };
        _status.Text = size + position + hint;
    }

    private static string colorText(Color c) => $"RGBA {c.R},{c.G},{c.B},{c.A}";

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var focusInGrid = _properties.ContainsFocus;
        switch (keyData)
        {
            case Keys.Control | Keys.Z: undo(); return true;
            case Keys.Control | Keys.Y: redo(); return true;
            case Keys.Enter when !focusInGrid && _layerSource is not null: Ui.Guard(this, commitLayer); return true;
            case Keys.Escape when _layerSource is not null: discardLayer(); return true;
            case Keys.T when !focusInGrid: setTool(Tool.Text); return true;
            case Keys.E when !focusInGrid: setTool(Tool.Erase); return true;
            case Keys.F when !focusInGrid: setTool(Tool.Fill); return true;
            case Keys.I when !focusInGrid: setTool(Tool.Eyedropper); return true;
            case Keys.V when !focusInGrid: setTool(Tool.Move); return true;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down when !focusInGrid && _layerSource is not null:
                var step = ModifierKeys.HasFlag(Keys.Shift) ? 10 : 1;
                var dx = keyData == Keys.Left ? -step : keyData == Keys.Right ? step : 0;
                var dy = keyData == Keys.Up ? -step : keyData == Keys.Down ? step : 0;
                _canvas.LayerPosition = new PointF(_canvas.LayerPosition.X + dx, _canvas.LayerPosition.Y + dy);
                _canvas.Invalidate(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image.Dispose(); _layerSource?.Dispose(); _watcher?.Dispose();
            foreach (var bitmap in _undo) bitmap.Dispose();
            foreach (var bitmap in _redo) bitmap.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Settings of a floating image layer.</summary>
    private sealed class LayerSettings(TextureEditorForm owner)
    {
        [System.ComponentModel.Category("Image layer"), System.ComponentModel.Description("1 = original size.")]
        public float Scale { get => owner._canvas.LayerScale; set { owner._canvas.LayerScale = Math.Clamp(value, 0.01f, 32); owner._canvas.Invalidate(); } }

        [System.ComponentModel.Category("Image layer"), System.ComponentModel.Description("0–100.")]
        public float Opacity { get => owner._canvas.LayerOpacity * 100; set { owner._canvas.LayerOpacity = Math.Clamp(value, 0, 100) / 100; owner._canvas.Invalidate(); } }

        [System.ComponentModel.Category("Image layer")]
        public float X { get => owner._canvas.LayerPosition.X; set { owner._canvas.LayerPosition = new PointF(value, owner._canvas.LayerPosition.Y); owner._canvas.Invalidate(); } }

        [System.ComponentModel.Category("Image layer")]
        public float Y { get => owner._canvas.LayerPosition.Y; set { owner._canvas.LayerPosition = new PointF(owner._canvas.LayerPosition.X, value); owner._canvas.Invalidate(); } }
    }
}

/// <summary>Texture resolution: scale the image, or change the canvas keeping pixels.</summary>
internal sealed class SizeDialog : Form
{
    private readonly NumericUpDown _width = new() { Minimum = 1, Maximum = Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension, Width = 90 };
    private readonly NumericUpDown _height = new() { Minimum = 1, Maximum = Waddamburo.Formats.Nut.NutTextureWriter.MaxDimension, Width = 90 };
    private readonly CheckBox _lock = new() { Text = "Keep aspect ratio", Checked = true, AutoSize = true };
    private readonly RadioButton _scale = new() { Text = "Scale the image (change resolution; on-screen size is unchanged)", Checked = true, AutoSize = true };
    private readonly RadioButton _canvas = new() { Text = "Change the canvas (keep pixels; the element's art shifts on screen)", AutoSize = true };
    private readonly CheckBox _center = new() { Text = "Centre the old pixels", Checked = true, AutoSize = true };
    private bool _updating;

    public SizeDialog(Size current, Size original)
    {
        Theme.BeginScaling(this);
        Text = "Texture size"; Width = 560; Height = 300; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
        _width.Value = current.Width; _height.Value = current.Height;
        var ratio = (decimal)current.Width / current.Height;
        _width.ValueChanged += (_, _) => { if (_lock.Checked && !_updating) { _updating = true; _height.Value = Math.Clamp(Math.Round(_width.Value / ratio), 1, _height.Maximum); _updating = false; } };
        _height.ValueChanged += (_, _) => { if (_lock.Checked && !_updating) { _updating = true; _width.Value = Math.Clamp(Math.Round(_height.Value * ratio), 1, _width.Maximum); _updating = false; } };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), WrapContents = false };
        var size = new FlowLayoutPanel { AutoSize = true };
        size.Controls.AddRange([new Label { Text = "Width", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _width,
            new Label { Text = "Height", AutoSize = true, Margin = new Padding(12, 6, 3, 3) }, _height, _lock]);
        var presets = new FlowLayoutPanel { AutoSize = true };
        foreach (var factor in new[] { 0.5f, 1f, 1.5f, 2f, 3f, 4f })
        {
            var f = factor;
            var b = new Button { Text = f == 1 ? "Original" : $"{f:0.#}×", AutoSize = true };
            b.Click += (_, _) => { _updating = true; _width.Value = Math.Clamp((int)Math.Round(original.Width * f), 1, (int)_width.Maximum); _height.Value = Math.Clamp((int)Math.Round(original.Height * f), 1, (int)_height.Maximum); _updating = false; };
            presets.Controls.Add(b);
        }
        layout.Controls.AddRange([size, new Label { Text = "Relative to the original game size:", AutoSize = true }, presets, _scale, _canvas, _center]);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(layout); Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        Theme.Apply(this);
    }

    public Size NewSize => new((int)_width.Value, (int)_height.Value);
    public bool ScaleImage => _scale.Checked;
    public bool Centered => _center.Checked;
}
