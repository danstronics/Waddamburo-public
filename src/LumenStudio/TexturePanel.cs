using LumenStudio.Core;
using Waddamburo.Formats.Nut;

namespace LumenStudio;

/// <summary>Textures drawn by the selection (or the whole movie) with edit, replace, export, revert and clone controls.</summary>
internal sealed class TexturePanel : UserControl
{
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false };
    private readonly ImageList _thumbs = new() { ImageSize = new Size(72, 72), ColorDepth = ColorDepth.Depth32Bit };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Top, Height = 190, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(44, 47, 56) };
    private readonly Label _info = new() { Dock = DockStyle.Top, Height = 92, Padding = new Padding(4), ForeColor = Theme.Foreground };
    private readonly CheckBox _wholeMovie = new() { Text = "All textures of the movie", AutoSize = true, Dock = DockStyle.Top };
    private readonly CheckBox _unlinked = new() { Text = "Unlinked: clone propagation skips this texture", AutoSize = true };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Top, Height = 70, Padding = new Padding(0, 2, 0, 2) };
    private MovieController? _movie;
    private IReadOnlyList<int> _shown = [];
    private bool _updating;

    public TexturePanel()
    {
        _list.LargeImageList = _thumbs;
        _buttons.Controls.Add(Ui.Button("Edit…", () => Edit?.Invoke(), "Open the texture editor (Ctrl+T)"));
        _buttons.Controls.Add(Ui.Button("Replace…", () => Replace?.Invoke(), "Replace with an image file of any size"));
        _buttons.Controls.Add(Ui.Button("Export PNG…", () => Export?.Invoke()));
        _buttons.Controls.Add(Ui.Button("Revert", () => Revert?.Invoke(), "Restore the original game texture"));
        _buttons.Controls.Add(Ui.Button("Clones…", () => ShowClones?.Invoke(), "Every texture with the same original image"));
        _buttons.Controls.Add(_unlinked);
        Controls.Add(_list);
        Controls.Add(_buttons);
        Controls.Add(_info);
        Controls.Add(_preview);
        Controls.Add(_wholeMovie);
        _list.SelectedIndexChanged += (_, _) => { if (!_updating) showSelected(); };
        _list.DoubleClick += (_, _) => Edit?.Invoke();
        _wholeMovie.CheckedChanged += (_, _) => Refresh(_lastSelection);
        _unlinked.CheckedChanged += (_, _) =>
        {
            if (_updating || SelectedTexture is not { } id || _movie is null) return;
            _movie.Studio.SetUnlinked(id, _unlinked.Checked);
            showSelected();
        };
    }

    private Selection? _lastSelection;

    public event Action? Edit;
    public event Action? Replace;
    public event Action? Export;
    public event Action? Revert;
    public event Action? ShowClones;

    public TextureId? SelectedTexture => _movie is null || _list.SelectedIndices.Count == 0 ? null : _movie.GlobalTexture(_shown[_list.SelectedIndices[0]]);

    public int? SelectedLocal => _list.SelectedIndices.Count == 0 ? null : _shown[_list.SelectedIndices[0]];

    public void SetMovie(MovieController? movie)
    {
        _movie = movie;
        Refresh(null);
    }

    public void Refresh(Selection? selection)
    {
        _lastSelection = selection;
        if (_movie is null) { clear(); return; }
        var previous = SelectedLocal;
        _shown = _wholeMovie.Checked || selection is null ? Enumerable.Range(0, _movie.TextureCount).ToArray() : _movie.TexturesUnder(selection);
        _updating = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var image in _thumbs.Images.Cast<Image>().ToArray()) image.Dispose();
            _thumbs.Images.Clear();
            // Thumbnails for a whole 800-texture movie would take a while; show the first 300.
            foreach (var local in _shown.Take(300))
            {
                var texture = _movie.Textures[local];
                _thumbs.Images.Add(thumbnail(texture.Rgba.Span, texture.Width, texture.Height));
                var id = _movie.GlobalTexture(local);
                var mark = _movie.Studio.Pending.Textures.ContainsKey(id) ? "✱ " : _movie.Studio.Index?.Get(id)?.SignificantlyModified == true ? "● " : "";
                _list.Items.Add(new ListViewItem($"{mark}{local}", _thumbs.Images.Count - 1));
            }
        }
        finally { _list.EndUpdate(); _updating = false; }
        var index = previous is int p ? Array.IndexOf(_shown.ToArray(), p) : -1;
        if (index < 0 && _list.Items.Count > 0) index = 0;
        if (index >= 0 && index < _list.Items.Count) { _list.Items[index].Selected = true; _list.EnsureVisible(index); }
        else showSelected();
    }

    private void showSelected()
    {
        if (_movie is null || SelectedLocal is not int local) { _preview.Image?.Dispose(); _preview.Image = null; _info.Text = _movie is null ? "" : "No texture under the selection."; return; }
        var texture = _movie.Textures[local];
        var old = _preview.Image;
        _preview.Image = Imaging.ToBitmap(texture.Rgba.Span, texture.Width, texture.Height);
        old?.Dispose();
        var id = _movie.GlobalTexture(local);
        var entry = _movie.Studio.Index?.Get(id);
        var pending = _movie.Studio.Pending.Textures.ContainsKey(id);
        var clones = _movie.Studio.Index is { } index ? index.Family(id).Count - 1 : -1;
        var state = pending ? "edited (unsaved)" : entry?.SignificantlyModified == true ? "modified in the overlay" : entry?.Modified == true ? "re-encoded (no visible change)" : "original";
        var original = entry is null ? "" : $" · original {entry.Width}×{entry.Height} {format(entry.Format)}";
        _info.Text = $"{entry?.Name ?? $"texture {local}"}\r\nLocal {local} · archive #{id.Index} · {texture.Width}×{texture.Height}{original}\r\nState: {state}\r\n"
            + (clones < 0 ? "Clones: scan the project (Textures › Scan clones)" : clones == 0 ? "Clones: none" : $"Clones: {clones} other texture{(clones == 1 ? "" : "s")} share this original image{(entry?.Uniform == true ? " (blank/solid: not propagated by default)" : "")}");
        _updating = true;
        _unlinked.Checked = _movie.Studio.Project.Unlinked.Contains(id.Key);
        _unlinked.Enabled = clones > 0;
        _updating = false;
    }

    private static string format(NutPixelFormat format) => format switch { NutPixelFormat.Bc3 => "DXT5", NutPixelFormat.Bc1 => "DXT1", _ => "ARGB" };

    private void clear()
    {
        _list.Items.Clear(); _thumbs.Images.Clear(); _shown = [];
        _preview.Image?.Dispose(); _preview.Image = null; _info.Text = "";
    }

    private static Bitmap thumbnail(ReadOnlySpan<byte> rgba, int width, int height)
    {
        using var full = Imaging.ToBitmap(rgba, width, height);
        var thumb = new Bitmap(72, 72);
        using var g = Graphics.FromImage(thumb);
        Imaging.Checkerboard(g, new RectangleF(0, 0, 72, 72), 6);
        var scale = Math.Min(70f / width, 70f / height);
        var w = Math.Max(1, width * scale); var h = Math.Max(1, height * scale);
        g.InterpolationMode = scale >= 1 ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(full, (72 - w) / 2, (72 - h) / 2, w, h);
        return thumb;
    }
}
