using System.Text.Json;
using System.Text.Json.Serialization;
using LumenStudio.Core;
using Waddamburo.Formats.Nut;

namespace LumenStudio;

/// <summary>Per-user application settings: recent projects and tool paths.</summary>
internal sealed class AppSettings
{
    public List<string> RecentProjects { get; set; } = [];
    public string? WaddamburoExecutable { get; set; }
    /// <summary>Lumen Author: the game's USRDIR whose art projects borrow (read only).</summary>
    public string? AuthoringGameRoot { get; set; }
    public bool PreviewScripts { get; set; }
    public bool ShowPlaceholders { get; set; } = true;
    public bool MoveWholeTrack { get; set; } = true;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumenStudio", "settings.json");

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), AppJson.Default.AppSettings) ?? new() : new(); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try { AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, AppJson.Default.AppSettings)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void AddRecent(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 12) RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
        Save();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJson : JsonSerializerContext;

/// <summary>Creates a project: original data, overlay output and project file.</summary>
internal sealed class NewProjectDialog : Form
{
    private readonly TextBox _base = new() { Width = 520 };
    private readonly TextBox _output = new() { Width = 520 };
    private readonly TextBox _project = new() { Width = 520 };

    public NewProjectDialog()
    {
        Theme.BeginScaling(this);
        Text = "New LumenStudio project"; Width = 760; Height = 380; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12), AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void row(string label, TextBox box, Action browse, string hint)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 8, 3) });
            layout.Controls.Add(box);
            layout.Controls.Add(Ui.Button("Browse…", browse));
            layout.Controls.Add(new Label());
            layout.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 0, 3, 10) });
            layout.Controls.Add(new Label());
        }
        row("Original game data", _base, () => folder(_base, "Choose the unmodified USRDIR (or its data/lumendata/packed folder)"),
            "Unmodified files: clone detection and the base every change is compared against. Never written.");
        row("Output (overlay)", _output, () => folder(_output, "Choose where modified archives are written"),
            "Receives only the modified archives, in the same folder structure: copy it over the game to install.");
        row("Project file", _project, () =>
        {
            using var dialog = new SaveFileDialog { Filter = "LumenStudio project|*.lstudio", FileName = "translation.lstudio" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _project.Text = dialog.FileName;
        }, "Stores settings, the clone index cache and the edit journal (next to it, in <name>.lstudio.data).");
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var create = new Button { Text = "Create", Width = 100, Height = 28 };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        create.Click += (_, _) => Ui.Guard(this, () =>
        {
            if (PackedFolder.Resolve(_base.Text) is null) throw new DirectoryNotFoundException("The original data folder has no data/lumendata/packed folder.");
            if (_output.Text.Length == 0 || _project.Text.Length == 0) throw new ArgumentException("Choose an output folder and a project file.");
            Project = StudioProject.Create(_project.Text, _base.Text, outputPacked());
            DialogResult = DialogResult.OK;
        });
        buttons.Controls.AddRange([cancel, create]);
        Controls.Add(layout); Controls.Add(buttons);
        CancelButton = cancel;
        Theme.Apply(this);
    }

    public StudioProject? Project { get; private set; }

    /// <summary>A USRDIR-shaped output gets the same data/lumendata/packed structure as the game.</summary>
    private string outputPacked()
    {
        var full = Path.GetFullPath(_output.Text);
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(full)).Equals("packed", StringComparison.OrdinalIgnoreCase)) return full;
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(full)).Equals("lumendata", StringComparison.OrdinalIgnoreCase)) return Path.Combine(full, "packed");
        return Path.Combine(full, "data", "lumendata", "packed");
    }

    private void folder(TextBox box, string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
    }
}

/// <summary>Project paths, the Waddamburo executable and linked layers.</summary>
internal sealed class ProjectSettingsDialog : Form
{
    private readonly StudioProject _project;
    private readonly ListView _layers = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = false, MultiSelect = false };
    private readonly TextBox _exe = new() { Width = 480 };
    private readonly TextBox _font = new() { Width = 480 };
    private readonly TextBox _gameRoot = new() { Width = 480 };

    public ProjectSettingsDialog(StudioProject project, AppSettings settings)
    {
        Theme.BeginScaling(this);
        _project = project;
        Text = "Project settings"; Width = 900; Height = 560; StartPosition = FormStartPosition.CenterParent;
        var info = new Label
        {
            Dock = DockStyle.Top, Height = 70, Padding = new Padding(10),
            Text = $"Original data: {project.BasePacked}\r\nOverlay output: {project.OutputPacked}\r\nProject: {project.Path}",
        };
        _layers.Columns.Add("Layer", 120); _layers.Columns.Add("Source", 300); _layers.Columns.Add("Output", 300); _layers.Columns.Add("Movies", 90); _layers.Columns.Add("Resize", 60);
        var layerButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38 };
        layerButtons.Controls.Add(Ui.Button("Add linked layer…", addLayer, "A separately modded copy of some archives (e.g. a custom song select) that should receive clone textures too"));
        layerButtons.Controls.Add(Ui.Button("Toggle movie protection", () => toggle(l => l.ProtectMovies = !l.ProtectMovies)));
        layerButtons.Controls.Add(Ui.Button("Toggle resize", () => toggle(l => l.AllowResize = !l.AllowResize)));
        layerButtons.Controls.Add(Ui.Button("Remove", () => toggle(l => _project.Linked.Remove(l))));
        var layers = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
        layers.Controls.Add(_layers); layers.Controls.Add(layerButtons); layers.Controls.Add(Ui.Caption("Linked layers"));
        var tools = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 110, ColumnCount = 3, Padding = new Padding(10, 4, 10, 4) };
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void row(string label, TextBox box, Action browse) { tools.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 6, 3) }); tools.Controls.Add(box); tools.Controls.Add(Ui.Button("Browse…", browse)); }
        _exe.Text = project.WaddamburoExecutable ?? settings.WaddamburoExecutable ?? "";
        _font.Text = project.FontPath ?? "";
        _gameRoot.Text = project.GameRoot ?? PackedFolder.GameRootOf(project.BasePacked) ?? "";
        row("Waddamburo executable", _exe, () => file(_exe, "Waddamburo|Waddamburo*.exe|Programs|*.exe"));
        row("Game USRDIR for previews", _gameRoot, () => folder(_gameRoot));
        row("Title font (font.ttf)", _font, () => file(_font, "Fonts|*.ttf;*.otf|All files|*.*"));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var save = new Button { Text = "Save", Width = 100, Height = 28 };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Ui.Guard(this, () =>
        {
            _project.WaddamburoExecutable = _exe.Text.Length == 0 ? null : _exe.Text;
            _project.FontPath = _font.Text.Length == 0 ? null : _font.Text;
            _project.GameRoot = _gameRoot.Text.Length == 0 ? null : _gameRoot.Text;
            _project.Validate();
            _project.Save();
            if (_project.WaddamburoExecutable is not null) { settings.WaddamburoExecutable = _project.WaddamburoExecutable; settings.Save(); }
            DialogResult = DialogResult.OK;
        });
        buttons.Controls.AddRange([cancel, save]);
        Controls.Add(layers); Controls.Add(tools); Controls.Add(info); Controls.Add(buttons);
        CancelButton = cancel;
        Theme.Apply(this);
        reload();
    }

    private void reload()
    {
        _layers.Items.Clear();
        foreach (var layer in _project.Linked)
            _layers.Items.Add(new ListViewItem([layer.Name, layer.SourcePacked, layer.OutputPacked, layer.ProtectMovies ? "protected" : "editable", layer.AllowResize ? "yes" : "no"]) { Tag = layer });
    }

    private void toggle(Action<LinkedLayer> action)
    {
        if (_layers.SelectedItems.Count == 0 || _layers.SelectedItems[0].Tag is not LinkedLayer layer) return;
        action(layer); reload();
    }

    private void addLayer()
    {
        using var source = new FolderBrowserDialog { Description = "Folder with the linked layer's archives (USRDIR, data, lumendata or packed)", UseDescriptionForTitle = true };
        if (source.ShowDialog(this) != DialogResult.OK) return;
        var packed = PackedFolder.Resolve(source.SelectedPath) ?? source.SelectedPath;
        if (!PackedFolder.Archives(packed).Any()) throw new InvalidOperationException("No .ddp archives in that folder.");
        using var output = new FolderBrowserDialog { Description = "Where this layer's modified archives are written (a separate folder)", UseDescriptionForTitle = true };
        if (output.ShowDialog(this) != DialogResult.OK) return;
        var name = Ui.Prompt(this, "Layer name", "A short name for this layer:", new DirectoryInfo(PackedFolder.GameRootOf(packed) ?? packed).Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var outputPacked = PackedFolder.Resolve(output.SelectedPath) ?? Path.Combine(output.SelectedPath, "data", "lumendata", "packed");
        _project.Linked.Add(new LinkedLayer { Name = name.Trim(), SourcePacked = packed, OutputPacked = outputPacked });
        try { _project.Validate(); }
        catch { _project.Linked.RemoveAt(_project.Linked.Count - 1); throw; }
        reload();
    }

    private void file(TextBox box, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
    }

    private void folder(TextBox box)
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
    }
}

/// <summary>Reviews a clone-propagation plan: every target with before/after previews; uncheck to skip.</summary>
internal sealed class SyncDialog : Form
{
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, MultiSelect = false, HideSelection = false };
    private readonly PictureBox _before = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(44, 47, 56) };
    private readonly PictureBox _after = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(44, 47, 56) };
    private readonly StudioSession _studio;

    public SyncDialog(StudioSession studio, IReadOnlyList<SyncAction> plan)
    {
        Theme.BeginScaling(this);
        _studio = studio;
        Text = $"Sync clones · most recent edit wins · {plan.Count} texture(s)"; Width = 1200; Height = 760; StartPosition = FormStartPosition.CenterParent;
        _list.Columns.Add("Target", 330); _list.Columns.Add("Receives from", 330); _list.Columns.Add("Edited", 130); _list.Columns.Add("Note", 200);
        foreach (var action in plan.OrderBy(a => a.Target.Id.Archive.ToString(), StringComparer.Ordinal).ThenBy(a => a.Target.Id.Index))
            _list.Items.Add(new ListViewItem([
                $"{action.Target.Id.Archive} #{action.Target.Id.Index} {action.Target.Name}",
                $"{action.Source.Id.Archive} #{action.Source.Id.Index} {action.Source.Name}",
                action.SourceTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                action.OverwritesEdit ? "overwrites an older edit" : ""])
            { Tag = action, Checked = true, ForeColor = action.OverwritesEdit ? Theme.Modified : Theme.Foreground });
        var previews = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 220, ColumnCount = 2 };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = new Panel { Dock = DockStyle.Fill }; left.Controls.Add(_before); left.Controls.Add(Ui.Caption("Target now"));
        var right = new Panel { Dock = DockStyle.Fill }; right.Controls.Add(_after); right.Controls.Add(Ui.Caption("After sync"));
        previews.Controls.Add(left, 0, 0); previews.Controls.Add(right, 1, 0);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        top.Controls.Add(Ui.Button("Check all", () => { foreach (ListViewItem item in _list.Items) item.Checked = true; }));
        top.Controls.Add(Ui.Button("Uncheck all", () => { foreach (ListViewItem item in _list.Items) item.Checked = false; }));
        top.Controls.Add(Ui.Button("Uncheck overwrites", () => { foreach (ListViewItem item in _list.Items) if (((SyncAction)item.Tag!).OverwritesEdit) item.Checked = false; }));
        top.Controls.Add(new Label { AutoSize = true, Margin = new Padding(12, 9, 3, 3), ForeColor = Theme.Muted, Text = "Each clone family copies its most recently edited member to every other member. Unchecked rows are skipped." });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var apply = new Button { Text = "Queue changes", Width = 130, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([cancel, apply]);
        Controls.Add(_list); Controls.Add(top); Controls.Add(previews); Controls.Add(buttons);
        AcceptButton = apply; CancelButton = cancel;
        Theme.Apply(this);
        _list.SelectedIndexChanged += (_, _) => Ui.Guard(this, preview);
    }

    public IEnumerable<SyncAction> Selected => _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (SyncAction)i.Tag!);

    private void preview()
    {
        if (_list.SelectedItems.Count == 0) return;
        var action = (SyncAction)_list.SelectedItems[0].Tag!;
        var old1 = _before.Image; var old2 = _after.Image;
        _before.Image = Imaging.ToBitmap(_studio.TextureImage(action.Target.Id));
        _after.Image = Imaging.ToBitmap(_studio.TextureImage(action.Source.Id));
        old1?.Dispose(); old2?.Dispose();
    }
}

/// <summary>Every texture sharing one original image, with link state and quick navigation.</summary>
internal sealed class CloneBrowserForm : Form
{
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, MultiSelect = false, HideSelection = false };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Right, Width = 360, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(44, 47, 56) };
    private readonly StudioSession _studio;
    private bool _updating;

    public CloneBrowserForm(StudioSession studio, TextureId id)
    {
        Theme.BeginScaling(this);
        _studio = studio;
        var family = studio.RequireIndex().Family(id);
        Text = $"Clones of {id} · {family.Count} texture(s)"; Width = 1100; Height = 600; StartPosition = FormStartPosition.CenterParent;
        _list.Columns.Add("Linked", 60); _list.Columns.Add("Texture", 380); _list.Columns.Add("Name", 220); _list.Columns.Add("Size", 90); _list.Columns.Add("State", 160);
        _updating = true;
        foreach (var member in family)
        {
            var pending = studio.Pending.Textures.ContainsKey(member.Id);
            var state = pending ? "edited (unsaved)" : member.SignificantlyModified ? "modified" : member.Modified ? "re-encoded" : "original";
            _list.Items.Add(new ListViewItem(["", member.Id.ToString(), member.Name ?? "", $"{member.CurrentWidth}×{member.CurrentHeight}", state])
            { Tag = member, Checked = !studio.Project.Unlinked.Contains(member.Id.Key), Font = member.Id == id ? Theme.BoldFont : Theme.UiFont });
        }
        _updating = false;
        _list.ItemChecked += (_, e) => { if (!_updating) Ui.Guard(this, () => studio.SetUnlinked(((TextureEntry)e.Item.Tag!).Id, !e.Item.Checked)); };
        _list.SelectedIndexChanged += (_, _) => Ui.Guard(this, () =>
        {
            if (_list.SelectedItems.Count == 0) return;
            var old = _preview.Image;
            _preview.Image = Imaging.ToBitmap(_studio.TextureImage(((TextureEntry)_list.SelectedItems[0].Tag!).Id));
            old?.Dispose();
        });
        var note = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8), ForeColor = Theme.Muted,
            Text = "Checked clones follow every edit of this image (most recent edit wins). Uncheck a clone to keep it independent." };
        Controls.Add(_list); Controls.Add(_preview); Controls.Add(note);
        Theme.Apply(this);
    }
}

/// <summary>A scrollable text report (validation, save results).</summary>
internal sealed class ReportForm : Form
{
    public ReportForm(string title, string text)
    {
        Theme.BeginScaling(this);
        Text = title; Width = 980; Height = 640; StartPosition = FormStartPosition.CenterParent;
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.MonoFont, Text = text };
        var close = new Button { Text = "Close", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.OK };
        Controls.Add(box); Controls.Add(close);
        AcceptButton = close; CancelButton = close;
        Theme.Apply(this);
        box.Select(0, 0);
    }
}

/// <summary>Hardware-invariant validation of every overlay archive.</summary>
internal static class Validation
{
    public static string Report(StudioSession studio)
    {
        var text = new System.Text.StringBuilder();
        var errors = 0; var count = 0;
        foreach (var archive in studio.Store.Archives().Where(studio.Store.IsModified))
        {
            count++;
            var current = studio.Store.ReadCurrent(archive);
            var original = studio.Store.ReadSource(archive);
            var kind = ArchiveStore.Kind(archive);
            var issues = CompatibilityChecker.Check(current, original, kind);
            var failed = issues.Any(i => i.Severity == IssueSeverity.Error);
            errors += failed ? 1 : 0;
            var after = TextureContainers.Textures(current, kind);
            var before = TextureContainers.Textures(original, kind);
            var resized = after.Zip(before).Count(p => p.First.Width != p.Second.Width || p.First.Height != p.Second.Height);
            text.AppendLine($"{(failed ? "FAIL" : "ok  ")}  {archive}  ({after.Sum(t => (long)t.DataSize) / 1048576.0:0.0} MiB textures{(resized > 0 ? $", {resized} resized" : "")})");
            foreach (var issue in issues) text.AppendLine($"        {issue.Severity}: {issue.Message}");
        }
        text.Insert(0, $"{count} modified archive(s), {errors} with errors.\r\nChecked: DDP directory and trailer (largest-texture load buffer), 16-byte texture layout, NTP3 v1 headers,\r\nformat and size limits (4096), F007 texture sizes, LMB references and texture ranges.\r\n\r\n");
        return text.ToString();
    }

    public static string Format(NutPixelFormat format) => format switch { NutPixelFormat.Bc3 => "DXT5", NutPixelFormat.Bc1 => "DXT1", _ => "ARGB" };
}
