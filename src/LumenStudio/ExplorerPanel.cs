using LumenStudio.Core;
using Waddamburo.Formats.Ddp;

namespace LumenStudio;

/// <summary>Archive tree by layer and folder; each archive lists its movies. Marks overlay and pending edits.</summary>
internal sealed class ExplorerPanel : UserControl
{
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true };
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter archives…" };
    private readonly CheckBox _modifiedOnly = new() { Text = "Modified only", AutoSize = true, Dock = DockStyle.Top };
    private readonly Dictionary<ArchiveId, TreeNode> _archiveNodes = [];
    private StudioSession? _studio;

    public ExplorerPanel()
    {
        Controls.Add(_tree); Controls.Add(_modifiedOnly); Controls.Add(_filter);
        _tree.BeforeExpand += (_, e) => { if (e.Node is { } node) Ui.Guard(this, () => expand(node)); };
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node is { } node) Ui.Guard(this, () => activate(node)); };
        _tree.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _tree.SelectedNode is { } node) Ui.Guard(this, () => activate(node)); };
        _filter.TextChanged += (_, _) => Reload();
        _modifiedOnly.CheckedChanged += (_, _) => Reload();
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right && e.Node is { } node) { _tree.SelectedNode = node; showMenu(node, e.Location); } };
    }

    public event Action<ArchiveId, string>? MovieOpened;
    public event Action<ArchiveId>? TextureArchiveOpened;
    public event Action<ArchiveId>? RevertRequested;

    public void SetSession(StudioSession? studio)
    {
        _studio = studio;
        Reload();
    }

    public void Reload()
    {
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear(); _archiveNodes.Clear();
            if (_studio is null) return;
            var filter = _filter.Text.Trim();
            foreach (var layer in _studio.Project.Layers)
            {
                var layerNode = new TreeNode(layer.Name switch
                {
                    StudioProject.MainLayer => "Lumen archives (lumendata)",
                    StudioProject.DataLayer => "Other game data (nutdata, don3d…)",
                    _ => $"Layer: {layer.Name}{(layer.ProtectMovies ? " (textures only)" : "")}",
                }) { NodeFont = Theme.BoldFont };
                var folders = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
                foreach (var path in layer.Name == StudioProject.DataLayer ? ArchiveStore.DataArchives(layer.SourcePacked) : PackedFolder.Archives(layer.SourcePacked))
                {
                    var id = new ArchiveId(layer.Name, path);
                    if (filter.Length > 0 && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_modifiedOnly.Checked && !_studio.Store.IsModified(id) && !_studio.Pending.Archives.Contains(id)) continue;
                    var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                    var fileName = Path.GetFileName(path);
                    // packeddata.ddp / nutdatapack.ndp are named by their folder; other files by their own name.
                    var container = fileName is "packeddata.ddp" or "nutdatapack.ndp" && folder.Length > 0;
                    var parentFolder = container ? Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? "" : folder;
                    var parent = layerNode;
                    if (parentFolder.Length > 0)
                    {
                        if (!folders.TryGetValue(parentFolder, out var group))
                        {
                            folders[parentFolder] = group = new TreeNode(parentFolder);
                            layerNode.Nodes.Add(group);
                        }
                        parent = group;
                    }
                    var node = new TreeNode(container ? Path.GetFileName(folder) : fileName) { Tag = id };
                    if (TextureContainers.KindOf(path) == ArchiveKind.Ddp) node.Nodes.Add(new TreeNode("…"));
                    parent.Nodes.Add(node);
                    _archiveNodes[id] = node;
                    decorate(node, id);
                }
                if (layerNode.Nodes.Count > 0) { _tree.Nodes.Add(layerNode); layerNode.Expand(); }
            }
        }
        finally { _tree.EndUpdate(); }
    }

    /// <summary>Refreshes overlay/pending markers without rebuilding the tree.</summary>
    public void RefreshMarkers()
    {
        if (_studio is null) return;
        var pending = _studio.Pending.Archives.ToHashSet();
        _tree.BeginUpdate();
        try { foreach (var (id, node) in _archiveNodes) decorate(node, id, pending); }
        finally { _tree.EndUpdate(); }
    }

    private void decorate(TreeNode node, ArchiveId id, HashSet<ArchiveId>? pendingArchives = null)
    {
        var pending = pendingArchives?.Contains(id) ?? _studio!.Pending.Archives.Contains(id);
        var modified = _studio!.Store.IsModified(id);
        var name = node.Text.TrimStart('✱', '●', ' ');
        var text = (pending ? "✱ " : modified ? "● " : "") + name;
        var color = pending ? Theme.Pending : modified ? Theme.Modified : Theme.Foreground;
        // Touch only changed nodes: every TreeView item update relays out the whole (large) tree.
        if (node.Text != text) node.Text = text;
        if (node.ForeColor != color) node.ForeColor = color;
        node.ToolTipText = pending ? "Unsaved edits" : modified ? "Modified (in the overlay output)" : "Original";
    }

    private void expand(TreeNode node)
    {
        if (node.Tag is not ArchiveId id || _studio is null || node.Nodes.Count != 1 || node.Nodes[0].Tag is not null) return;
        node.Nodes.Clear();
        foreach (var movie in moviesOf(id))
            node.Nodes.Add(new TreeNode(movie) { Tag = (id, movie) });
    }

    private IEnumerable<string> moviesOf(ArchiveId id)
    {
        if (_studio!.Store.PackList(id) is { Movies.Count: > 0 } list) return list.Movies.Select(m => m.Movie);
        return DdpArchive.Open(_studio.Store.ReadSource(id)).Index.Movies.Select(m => m.Name);
    }

    private void activate(TreeNode node)
    {
        switch (node.Tag)
        {
            case (ArchiveId id, string movie): MovieOpened?.Invoke(id, movie); break;
            case ArchiveId id when TextureContainers.KindOf(id) != ArchiveKind.Ddp:
                TextureArchiveOpened?.Invoke(id);
                break;
            case ArchiveId id:
                var movies = moviesOf(id).ToArray();
                if (movies.Length == 1) MovieOpened?.Invoke(id, movies[0]);
                else node.Expand();
                break;
        }
    }

    private void showMenu(TreeNode node, Point location)
    {
        if (node.Tag is not ArchiveId and not (ArchiveId, string)) return;
        var id = node.Tag is ArchiveId a ? a : (((ArchiveId, string))node.Tag).Item1;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Ui.Guard(this, () => activate(node)));
        menu.Items.Add("Show in Explorer", null, (_, _) => Ui.Guard(this, () =>
        {
            var path = _studio!.Store.CurrentPath(id);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        }));
        var revert = menu.Items.Add("Revert to original (delete overlay copy)…", null, (_, _) => RevertRequested?.Invoke(id));
        revert.Enabled = _studio!.Store.IsModified(id);
        Theme.Apply(menu);
        menu.Show(_tree, location);
    }
}
