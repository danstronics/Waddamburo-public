using LumenStudio.Core;

namespace LumenStudio;

/// <summary>Textures of an archive without movies (NDP packs, standalone NUTs): browse, edit, replace, clones.</summary>
internal sealed class TextureArchiveForm : Form
{
    private readonly StudioSession _studio;
    private readonly ArchiveId _archive;
    private readonly Action<string> _log;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false };
    private readonly ImageList _thumbs = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Top, Height = 260, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(44, 47, 56) };
    private readonly Label _info = new() { Dock = DockStyle.Top, Height = 90, Padding = new Padding(6) };
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter by name…" };
    private IReadOnlyList<TextureSlot> _slots = [];
    private List<TextureSlot> _shown = [];

    public TextureArchiveForm(StudioSession studio, ArchiveId archive, Action<string> log)
    {
        Theme.BeginScaling(this);
        _studio = studio; _archive = archive; _log = log;
        Text = $"Textures · {archive}"; Width = 1200; Height = 800; StartPosition = FormStartPosition.CenterParent;
        _thumbs.ImageSize = new Size(Theme.Scale(this, 96), Theme.Scale(this, 96));
        _list.LargeImageList = _thumbs;
        var side = new Panel { Dock = DockStyle.Right, Width = 380, Padding = new Padding(6) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 74 };
        buttons.Controls.Add(Ui.Button("Edit…", () => act(id => TextureCommands.Edit(this, _studio, id, _log))));
        buttons.Controls.Add(Ui.Button("Replace…", () => act(id => TextureCommands.Replace(this, _studio, id, _log))));
        buttons.Controls.Add(Ui.Button("Export PNG…", () => act(id => { TextureCommands.Export(this, _studio, id); return false; })));
        buttons.Controls.Add(Ui.Button("Revert", () => act(id => { TextureCommands.Revert(this, _studio, id, _log); return true; })));
        buttons.Controls.Add(Ui.Button("Clones…", () => act(id => { TextureCommands.ShowClones(this, _studio, id); return true; })));
        buttons.Controls.Add(Ui.Button("Export all…", () => TextureCommands.ExportAll(this, _studio, ids(), _log)));
        buttons.Controls.Add(Ui.Button("Import folder…", () => { TextureCommands.ImportFolder(this, _studio, ids(), _log); reload(); }));
        side.Controls.Add(buttons); side.Controls.Add(_info); side.Controls.Add(_preview);
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_list); left.Controls.Add(_filter);
        Controls.Add(left); Controls.Add(side);
        Theme.Apply(this);
        _list.SelectedIndexChanged += (_, _) => Ui.Guard(this, show);
        _list.DoubleClick += (_, _) => act(id => TextureCommands.Edit(this, _studio, id, _log));
        _filter.TextChanged += (_, _) => reload();
        _studio.Changed += onChanged;
        FormClosed += (_, _) => _studio.Changed -= onChanged;
        _slots = TextureContainers.Slots(_studio.ArchiveBytes(archive), ArchiveStore.Kind(archive), Path.GetFileName(archive.Path), _studio.Store.PackList(archive));
        reload();
    }

    private IReadOnlyList<TextureId> ids() => _slots.Select(s => new TextureId(_archive, s.Index)).ToArray();

    private TextureId? selected => _list.SelectedItems.Count == 0 ? null : new TextureId(_archive, ((TextureSlot)_list.SelectedItems[0].Tag!).Index);

    private void act(Func<TextureId, bool> action)
    {
        if (selected is not { } id) return;
        Ui.Guard(this, () => { if (action(id)) reload(); });
    }

    private void onChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(onChanged); return; }
        reload();
    }

    private void reload()
    {
        var keep = selected;
        var filter = _filter.Text.Trim();
        _shown = _slots.Where(s => filter.Length == 0 || (s.Name ?? s.Group).Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(1500).ToList();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var image in _thumbs.Images.Cast<Image>().ToArray()) image.Dispose();
            _thumbs.Images.Clear();
            foreach (var slot in _shown)
            {
                var id = new TextureId(_archive, slot.Index);
                _thumbs.Images.Add(thumbnail(_studio.TextureImage(id), _thumbs.ImageSize.Width));
                var mark = _studio.Pending.Textures.ContainsKey(id) ? "✱ " : _studio.Index?.Get(id)?.SignificantlyModified == true ? "● " : "";
                _list.Items.Add(new ListViewItem(mark + (slot.Name ?? $"#{slot.Index}"), _thumbs.Images.Count - 1) { Tag = slot });
            }
        }
        finally { _list.EndUpdate(); }
        var index = keep is { } k ? _shown.FindIndex(s => s.Index == k.Index) : 0;
        if (index >= 0 && index < _list.Items.Count) { _list.Items[index].Selected = true; _list.EnsureVisible(index); }
    }

    private void show()
    {
        if (selected is not { } id) return;
        var image = _studio.TextureImage(id);
        var old = _preview.Image;
        _preview.Image = Imaging.ToBitmap(image);
        old?.Dispose();
        var entry = _studio.Index?.Get(id);
        var clones = _studio.Index is { } index ? index.Family(id).Count - 1 : -1;
        _info.Text = $"{entry?.Name ?? $"#{id.Index}"}\r\n{image.Width}×{image.Height}" + (entry is null ? "" : $" · original {entry.Width}×{entry.Height} {Validation.Format(entry.Format)}") +
            $"\r\n{(clones < 0 ? "Scan clones to see copies." : clones == 0 ? "No clones." : $"{clones} clone(s) share this original image.")}";
    }

    private static Bitmap thumbnail(RgbaImage image, int size)
    {
        using var full = Imaging.ToBitmap(image);
        var thumb = new Bitmap(size, size);
        using var g = Graphics.FromImage(thumb);
        Imaging.Checkerboard(g, new RectangleF(0, 0, size, size), 6);
        var scale = Math.Min((size - 2f) / image.Width, (size - 2f) / image.Height);
        var w = Math.Max(1, image.Width * scale); var h = Math.Max(1, image.Height * scale);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(full, (size - w) / 2, (size - h) / 2, w, h);
        return thumb;
    }
}
