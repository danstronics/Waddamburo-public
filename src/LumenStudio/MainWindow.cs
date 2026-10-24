using System.Diagnostics;
using System.Globalization;
using LumenStudio.Core;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Runtime;

namespace LumenStudio;

internal sealed class MainWindow : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly StageView _stage = new();
    private readonly TimelineView _timeline = new() { Dock = DockStyle.Fill };
    private readonly ExplorerPanel _explorer = new() { Dock = DockStyle.Fill };
    private readonly TexturePanel _textures = new() { Dock = DockStyle.Fill };
    private readonly TextFieldPanel _text = new() { Dock = DockStyle.Fill };
    private readonly StringsPanel _strings = new() { Dock = DockStyle.Fill };
    private readonly ScriptsPanel _scripts = new() { Dock = DockStyle.Fill };
    private readonly PropertyGrid _properties = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true, PropertySort = PropertySort.Categorized };
    private readonly Label _elementInfo = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4) };
    private readonly TabControl _inspector = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _breadcrumb = new() { Dock = DockStyle.Top, Height = 28, WrapContents = false, AutoScroll = false, Padding = new Padding(4, 3, 4, 0) };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.MonoFont };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _pendingLabel = new();
    private readonly ToolStripStatusLabel _frameLabel = new();
    private readonly ToolStripButton _playButton = new("▶ Play") { ToolTipText = "Play / pause (Space). Clicking the stage while playing pauses and selects." };
    private readonly ToolStripButton _scriptsButton = new("Scripts on") { CheckOnClick = true, ToolTipText = "Run ActionScript in the preview (off shows the authored layout)" };
    private readonly ToolStripButton _wholeTrackButton = new("Move: whole track") { CheckOnClick = true, ToolTipText = "Dragging moves every key of the element's track (off: only the current frame's key)" };
    private readonly ToolStripButton _placeholdersButton = new("Host slots") { CheckOnClick = true, ToolTipText = "Outline text fields and host-drawn slots (titles, Don) that the game fills at runtime" };
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 8 };
    private readonly Stopwatch _playClock = new();
    private double _frameDebt;
    private StudioSession? _studio;
    private MovieController? _movie;
    private bool _refreshing;

    public MainWindow(string[] args)
    {
        Theme.BeginScaling(this);
        Text = "LumenStudio";
        Width = 1700; Height = 1000; MinimumSize = new Size(1200, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.UiFont;
        KeyPreview = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var menu = buildMenu();
        MainMenuStrip = menu;
        var toolbar = buildToolbar();

        // Left: explorer.
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_explorer); left.Controls.Add(Ui.Caption("Archives · double-click a movie"));
        // Centre: breadcrumb + stage, timeline below.
        var stageHost = new Panel { Dock = DockStyle.Fill };
        stageHost.Controls.Add(_stage); stageHost.Controls.Add(_breadcrumb);
        var centre = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 4, FixedPanel = FixedPanel.Panel2 };
        centre.Panel1.Controls.Add(stageHost);
        centre.Panel2.Controls.Add(_timeline);
        // Right: inspector tabs and log.
        var elementPage = new TabPage("Element");
        elementPage.Controls.Add(_properties); elementPage.Controls.Add(_elementInfo);
        var texturePage = new TabPage("Texture"); texturePage.Controls.Add(_textures);
        var textPage = new TabPage("Text"); textPage.Controls.Add(_text);
        var stringsPage = new TabPage("Strings"); stringsPage.Controls.Add(_strings);
        var scriptsPage = new TabPage("Scripts"); scriptsPage.Controls.Add(_scripts);
        _inspector.TabPages.AddRange([texturePage, elementPage, textPage, stringsPage, scriptsPage]);
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 4, FixedPanel = FixedPanel.Panel2 };
        right.Panel1.Controls.Add(_inspector);
        right.Panel2.Controls.Add(_log); right.Panel2.Controls.Add(Ui.Caption("Log"));

        var outer = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 4, FixedPanel = FixedPanel.Panel1 };
        var inner = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 4, FixedPanel = FixedPanel.Panel2 };
        outer.Panel1.Controls.Add(left);
        outer.Panel2.Controls.Add(inner);
        inner.Panel1.Controls.Add(centre);
        inner.Panel2.Controls.Add(right);

        var status = new StatusStrip { SizingGrip = false };
        status.Items.AddRange([_status, _frameLabel, _pendingLabel]);
        Controls.Add(outer); Controls.Add(toolbar); Controls.Add(menu); Controls.Add(status);
        Theme.Apply(this);
        Load += (_, _) =>
        {
            outer.SplitterDistance = Theme.Scale(this, 280);
            inner.SplitterDistance = Math.Max(Theme.Scale(this, 400), inner.Width - Theme.Scale(this, 430));
            centre.SplitterDistance = Math.Max(Theme.Scale(this, 300), centre.Height - Theme.Scale(this, 260));
            right.SplitterDistance = Math.Max(Theme.Scale(this, 300), right.Height - Theme.Scale(this, 150));
        };

        _scriptsButton.Checked = _settings.PreviewScripts;
        _wholeTrackButton.Checked = _settings.MoveWholeTrack;
        _placeholdersButton.Checked = _settings.ShowPlaceholders;
        _stage.ShowPlaceholders = _settings.ShowPlaceholders;
        wireEvents();
        updateTitle();
        log("LumenStudio ready. File › New project (original data + output overlay) or File › Open project.");

        Shown += (_, _) => Ui.Guard(this, () =>
        {
            var input = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (input is not null && input.EndsWith(StudioProject.Extension, StringComparison.OrdinalIgnoreCase)) openProject(input);
            else if (_settings.RecentProjects.FirstOrDefault(File.Exists) is { } recent && !args.Contains("--no-recent")) openProject(recent);
            if (args.Contains("--no-scripts")) _scriptsButton.Checked = false;
            var movieArg = args.FirstOrDefault(a => a.StartsWith("--movie=", StringComparison.Ordinal))?[8..];
            var archiveArg = args.FirstOrDefault(a => a.StartsWith("--archive=", StringComparison.Ordinal))?[10..];
            if (_studio is not null && archiveArg is not null)
                openMovie(new ArchiveId(StudioProject.MainLayer, archiveArg), movieArg ?? firstMovie(new ArchiveId(StudioProject.MainLayer, archiveArg)));
            if (args.FirstOrDefault(a => a.StartsWith("--frame=", StringComparison.Ordinal)) is { } frame && _movie is not null)
                _movie.Seek(int.Parse(frame[8..], CultureInfo.InvariantCulture));
            if (args.FirstOrDefault(a => a.StartsWith("--advance=", StringComparison.Ordinal)) is { } advance && _movie is not null)
            {
                _movie.Play();
                _movie.Advance(int.Parse(advance[10..], CultureInfo.InvariantCulture));
                _movie.Pause();
                refreshFrame();
            }
            if (args.FirstOrDefault(a => a.StartsWith("--pick=", StringComparison.Ordinal)) is { } pick && _movie is not null)
            {
                var xy = pick[7..].Split(',');
                select(_movie.Pick(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture)));
            }
            if (args.FirstOrDefault(a => a.StartsWith("--move=", StringComparison.Ordinal)) is { } move && _movie is not null)
            {
                var d = move[7..].Split(',');
                moveSelection(float.Parse(d[0], CultureInfo.InvariantCulture), float.Parse(d[1], CultureInfo.InvariantCulture), _wholeTrackButton.Checked);
            }
            if (args.FirstOrDefault(a => a.StartsWith("--edit-text=", StringComparison.Ordinal)) is { } editText && _movie is not null)
                demoTextureEdit(editText[12..], args.FirstOrDefault(a => a.StartsWith("--editor-capture=", StringComparison.Ordinal))?[17..]);
            if (args.Contains("--save")) saveAll();
            if (args.FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.Ordinal)) is { } tab)
                _inspector.SelectedIndex = Math.Clamp(int.Parse(tab[6..], CultureInfo.InvariantCulture), 0, _inspector.TabCount - 1);
            if (args.FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.Ordinal)) is { } capture)
                BeginInvoke(() => { Application.DoEvents(); captureWindow(capture[10..]); Close(); });
        });
        FormClosing += (_, e) =>
        {
            if (_studio is { Pending.IsEmpty: false } && !args.Any(a => a.StartsWith("--capture=", StringComparison.Ordinal)))
            {
                var answer = MessageBox.Show(this, $"Save {_studio.Pending.Archives.Count()} archive(s) with unsaved edits before closing?", "LumenStudio",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
                if (answer == DialogResult.Yes && !saveAll()) { e.Cancel = true; return; }
            }
            _settings.PreviewScripts = _scriptsButton.Checked; _settings.MoveWholeTrack = _wholeTrackButton.Checked; _settings.ShowPlaceholders = _placeholdersButton.Checked;
            _settings.Save();
        };
    }

    // ───────────────────────────── layout ─────────────────────────────

    private MenuStrip buildMenu()
    {
        var menu = new MenuStrip();
        ToolStripMenuItem root(string title) { var item = new ToolStripMenuItem(title); menu.Items.Add(item); return item; }
        ToolStripMenuItem item(ToolStripMenuItem parent, string text, Keys keys, Action action, string? shortcutText = null)
        {
            var child = new ToolStripMenuItem(text) { ShortcutKeys = keys, ShortcutKeyDisplayString = shortcutText };
            child.Click += (_, _) => Ui.Guard(this, action);
            parent.DropDownItems.Add(child);
            return child;
        }
        void separator(ToolStripMenuItem parent) => parent.DropDownItems.Add(new ToolStripSeparator());

        var file = root("&File");
        item(file, "New project…", Keys.Control | Keys.N, newProject);
        item(file, "Open project…", Keys.Control | Keys.O, () =>
        {
            using var dialog = new OpenFileDialog { Filter = "LumenStudio project|*.lstudio" };
            if (dialog.ShowDialog(this) == DialogResult.OK) openProject(dialog.FileName);
        });
        var recent = new ToolStripMenuItem("Recent projects");
        recent.DropDownOpening += (_, _) =>
        {
            recent.DropDownItems.Clear();
            foreach (var path in _settings.RecentProjects.Where(File.Exists))
            {
                var p = path;
                recent.DropDownItems.Add(p, null, (_, _) => Ui.Guard(this, () => openProject(p)));
            }
            if (recent.DropDownItems.Count == 0) recent.DropDownItems.Add("(none)").Enabled = false;
        };
        recent.DropDownItems.Add("(none)");
        file.DropDownItems.Add(recent);
        item(file, "Project settings…", Keys.None, projectSettings);
        separator(file);
        item(file, "Save all", Keys.Control | Keys.S, () => saveAll());
        item(file, "Discard unsaved edits of this archive", Keys.None, () =>
        {
            if (_movie is null || !Ui.Confirm(this, $"Discard every unsaved edit of {_movie.Archive}?")) return;
            requireStudio().Discard(_movie.Archive);
        });
        item(file, "Revert archive to original…", Keys.None, () => { if (_movie is not null) revertArchive(_movie.Archive); });
        separator(file);
        item(file, "Open output folder", Keys.None, () => Process.Start("explorer.exe", $"\"{Directory.CreateDirectory(requireStudio().Project.OutputPacked).FullName}\"")?.Dispose());
        item(file, "Exit", Keys.Alt | Keys.F4, Close);

        var edit = root("&Edit");
        item(edit, "Undo", Keys.Control | Keys.Z, () => requireStudio().Undo());
        item(edit, "Redo", Keys.Control | Keys.Y, () => requireStudio().Redo());
        separator(edit);
        item(edit, "Select parent", Keys.Alt | Keys.Up, () => { if (_movie?.Selected is { Level: > 1 } s) select(s.WithLevel(s.Level - 1)); });
        item(edit, "Select child", Keys.Alt | Keys.Down, () => { if (_movie?.Selected is { } s && s.Level < s.Path.Length - 1) select(s.WithLevel(s.Level + 1)); });
        item(edit, "Enter selected sprite", Keys.Control | Keys.Enter, enterSelection);
        item(edit, "Back to scene", Keys.Control | Keys.Back, () => { _movie?.ExitToScene(); refreshMovie(); });
        item(edit, "Deselect", Keys.None, () => select(null), "Esc");
        separator(edit);
        item(edit, "Duplicate element", Keys.Control | Keys.D, duplicate);
        item(edit, "Toggle element visibility", Keys.Control | Keys.H, toggleVisibility);
        item(edit, "Remove element from this frame", Keys.None, removeElement);

        var view = root("&View");
        item(view, "Fit stage", Keys.Control | Keys.D0, _stage.Fit);
        item(view, "Actual size", Keys.Control | Keys.D1, _stage.ActualSize);
        item(view, "Toggle grid", Keys.Control | Keys.G, () => { _stage.ShowGrid = !_stage.ShowGrid; _stage.Invalidate(); });
        item(view, "Save stage screenshot…", Keys.None, () =>
        {
            using var dialog = new SaveFileDialog { Filter = "PNG|*.png", FileName = "stage.png" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { using var bitmap = _stage.CaptureImage(); bitmap?.Save(dialog.FileName); }
        });

        var timeline = root("&Timeline");
        item(timeline, "Play / pause", Keys.None, togglePlay, "Space");
        item(timeline, "Rewind", Keys.None, () => seek(0), "Home");
        item(timeline, "Previous frame", Keys.None, () => step(-1), ",");
        item(timeline, "Next frame", Keys.None, () => step(1), ".");
        item(timeline, "Last frame", Keys.None, () => seek(int.MaxValue), "End");
        separator(timeline);
        item(timeline, "Insert keyframe", Keys.F6, insertKeyframe);
        item(timeline, "Delete keyframe", Keys.Shift | Keys.F6, deleteKeyframe);
        item(timeline, "Create animation (tween)…", Keys.Control | Keys.T, tween);
        item(timeline, "Add frame label…", Keys.None, addLabel);
        item(timeline, "Add frame script…", Keys.None, () => openScripts(null, newScript: true));
        item(timeline, "Extend timeline…", Keys.None, extendTimeline);

        var texture = root("Te&xture");
        item(texture, "Edit selected texture…", Keys.Control | Keys.E, editTexture);
        item(texture, "Replace selected texture with image…", Keys.Control | Keys.R, replaceTexture);
        item(texture, "Export selected texture…", Keys.None, exportTexture);
        item(texture, "Revert selected texture to original", Keys.None, revertTexture);
        separator(texture);
        item(texture, "Export all textures of this movie…", Keys.None, exportAll);
        item(texture, "Import edited PNG folder…", Keys.None, importFolder);
        separator(texture);
        item(texture, "Scan clones (rebuild index)", Keys.None, () => scanClones(force: true));
        item(texture, "Sync clones: most recent edit wins…", Keys.Control | Keys.Shift | Keys.Y, syncClones);
        item(texture, "Clone browser for selected texture…", Keys.None, showClones);

        var tools = root("&Tools");
        item(tools, "Validate output for hardware…", Keys.None, validate);
        separator(tools);
        item(tools, "Test movie in Waddamburo", Keys.F5, () => testMovie(layoutOnly: false));
        item(tools, "Test movie layout in Waddamburo (no scripts)", Keys.Shift | Keys.F5, () => testMovie(layoutOnly: true));
        item(tools, "Test full game in Waddamburo (saved changes)", Keys.Control | Keys.F5, testGame);
        separator(tools);
        item(tools, "Script editor…", Keys.Control | Keys.Shift | Keys.S, () => openScripts(null));

        var help = root("&Help");
        item(help, "Keyboard and mouse…", Keys.F1, () => new ReportForm("Keyboard and mouse", Help.Text).ShowDialog(this));
        item(help, "About LumenStudio", Keys.None, () => MessageBox.Show(this,
            "LumenStudio — Lumen (LMB) and texture editor for Gen3 Taiko no Tatsujin data.\r\nBuilt on Waddamburo's open Lumen runtime, parsers and renderer.\r\nWrites only to the overlay output; original files are never modified.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information));
        return menu;
    }

    private ToolStrip buildToolbar()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2), ImageScalingSize = new Size(16, 16) };
        void tool(string text, Action action, string tip) { var b = new ToolStripButton(text) { ToolTipText = tip }; b.Click += (_, _) => Ui.Guard(this, action); toolbar.Items.Add(b); }
        tool("💾 Save all", () => saveAll(), "Write modified archives to the overlay (Ctrl+S)");
        tool("↶", () => requireStudio().Undo(), "Undo (Ctrl+Z)");
        tool("↷", () => requireStudio().Redo(), "Redo (Ctrl+Y)");
        toolbar.Items.Add(new ToolStripSeparator());
        tool("⏮", () => seek(0), "Rewind (Home)");
        tool("◀", () => step(-1), "Previous frame (,)");
        _playButton.Click += (_, _) => Ui.Guard(this, togglePlay);
        toolbar.Items.Add(_playButton);
        tool("▶|", () => step(1), "Next frame (.)");
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_scriptsButton);
        toolbar.Items.Add(_placeholdersButton);
        toolbar.Items.Add(_wholeTrackButton);
        toolbar.Items.Add(new ToolStripSeparator());
        tool("✎ Edit texture", editTexture, "Edit the selected texture (Ctrl+E)");
        tool("⇄ Replace", replaceTexture, "Replace the selected texture with an image of any size (Ctrl+R)");
        tool("⟳ Sync clones", syncClones, "Propagate the most recent edit of every clone family");
        toolbar.Items.Add(new ToolStripSeparator());
        tool("✔ Validate", validate, "Check every output archive against the original hardware's invariants");
        tool("▶ Waddamburo", () => testMovie(layoutOnly: false), "Open this movie, with unsaved edits, in Waddamburo (F5)");
        tool("Fit", _stage.Fit, "Fit the stage (Ctrl+0)");
        return toolbar;
    }

    private void wireEvents()
    {
        _explorer.MovieOpened += (archive, movie) => Ui.Guard(this, () => openMovie(archive, movie));
        _explorer.TextureArchiveOpened += archive => Ui.Guard(this, () => new TextureArchiveForm(requireStudio(), archive, log).Show(this));
        _explorer.RevertRequested += archive => Ui.Guard(this, () => revertArchive(archive));
        _clock.Tick += (_, _) => Ui.Guard(this, tick);
        _scriptsButton.CheckedChanged += (_, _) => Ui.Guard(this, () => { _movie?.SetRunScripts(_scriptsButton.Checked); refreshFrame(); });
        _placeholdersButton.CheckedChanged += (_, _) => { _stage.ShowPlaceholders = _placeholdersButton.Checked; _stage.Invalidate(); };

        _stage.StageClicked += (point, e) => Ui.Guard(this, () =>
        {
            if (_movie is null) return;
            if (_movie.Playing) pause();
            var picked = _movie.Pick(point.X, point.Y);
            // Plain click selects the direct child of the viewed timeline, Ctrl+click the deepest element.
            if (picked is not null && !ModifierKeys.HasFlag(Keys.Control) && !ModifierKeys.HasFlag(Keys.Alt) && _movie.Selected is null)
                picked = picked.WithLevel(picked.Path.Length - 1);
            if (picked is not null && _movie.Selected is { } current && current.Contains(picked.Hit!) && !ModifierKeys.HasFlag(Keys.Control))
                picked = picked.WithLevel(current.Level);
            select(picked);
        });
        _stage.StageDoubleClicked += (_, _) => Ui.Guard(this, () =>
        {
            if (_movie?.Selected is not { } selection) return;
            if (_movie.Document.Definition.Sprites.Any(s => s.CharacterId == selection.Node.CharacterId)) enterSelection();
            else if (selection.Hit is { Kind: Waddamburo.Lumen.Rendering.LumenQuadOriginKind.Texture }) editTexture();
        });
        _stage.StageHover += point => Ui.Guard(this, () =>
        {
            if (_movie is null || point is null || _movie.Playing) { _stage.HoverBounds = null; _stage.Invalidate(); return; }
            var hit = _movie.Pick(point.Value.X, point.Value.Y);
            _stage.HoverBounds = hit is null ? null : _movie.SelectionBounds(hit);
            _status.Text = hit is null ? "" : $"{_movie.Describe(hit.Node)} · texture {hit.Hit?.TextureIndex.ToString(CultureInfo.InvariantCulture) ?? "-"} · click to select, Ctrl+click for the innermost element, double-click to enter/edit";
            _stage.Invalidate();
        });
        _stage.MoveCommitted += (dx, dy) => Ui.Guard(this, () => moveSelection(dx, dy, _wholeTrackButton.Checked));
        _stage.ResizeCommitted += (sx, sy, anchor) => Ui.Guard(this, () => resizeSelection(sx, sy, anchor));

        _timeline.FrameRequested += frame => Ui.Guard(this, () => { pause(); seek(frame); });
        _timeline.TrackClicked += (depth, frame) => Ui.Guard(this, () => selectDepth(depth));
        _timeline.MenuRequested += (depth, frame, location) => Ui.Guard(this, () => timelineMenu(depth, location));

        _properties.PropertyValueChanged += (_, _) => Ui.Guard(this, applyProperties);
        _textures.Edit += () => Ui.Guard(this, editTexture);
        _textures.Replace += () => Ui.Guard(this, replaceTexture);
        _textures.Export += () => Ui.Guard(this, exportTexture);
        _textures.Revert += () => Ui.Guard(this, revertTexture);
        _textures.ShowClones += () => Ui.Guard(this, showClones);
        _scripts.OpenEditor += action => Ui.Guard(this, () => openScripts(action));
        _scripts.GoToFrame += frame => Ui.Guard(this, () => { pause(); seek(frame); });
    }

    // ───────────────────────────── project ─────────────────────────────

    private StudioSession requireStudio() => _studio ?? throw new InvalidOperationException("Open or create a project first (File › New project).");
    private MovieController requireMovie() => _movie ?? throw new InvalidOperationException("Open a movie first: double-click it in the archive list.");

    private void newProject()
    {
        if (!maySwitchProject()) return;
        using var dialog = new NewProjectDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Project is null) return;
        openProject(dialog.Project.Path);
    }

    private bool maySwitchProject() => _studio is null || _studio.Pending.IsEmpty
        || Ui.Confirm(this, "The current project has unsaved edits. Switch project and discard them?");

    private void openProject(string path)
    {
        if (!maySwitchProject()) return;
        var project = StudioProject.Load(path);
        closeMovie();
        _studio = StudioSession.Open(project);
        _studio.Changed += onStudioChanged;
        _settings.AddRecent(project.Path);
        _explorer.SetSession(_studio);
        log($"Opened {project.Path}\r\n  original: {project.BasePacked}\r\n  output:   {project.OutputPacked}");
        foreach (var layer in project.Linked) log($"  linked layer '{layer.Name}': {layer.SourcePacked} → {layer.OutputPacked}{(layer.ProtectMovies ? " (textures only)" : "")}");
        updateTitle();
        scanClones(force: false);
    }

    private void projectSettings()
    {
        var studio = requireStudio();
        using var dialog = new ProjectSettingsDialog(studio.Project, _settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var path = studio.Project.Path;
        if (studio.Pending.IsEmpty) { _studio = null; openProject(path); }
        else log("Settings saved. Reopen the project to apply layer changes.");
    }

    private void scanClones(bool force)
    {
        var studio = requireStudio();
        if (!force && studio.Index is not null) return;
        using var progress = new ProgressForm("Scanning textures for clones…");
        progress.Show(this);
        var clock = Stopwatch.StartNew();
        var task = Task.Run(() => studio.BuildIndex(new Progress<IndexProgress>(p => progress.Report(p.Done, p.Total, p.Current))));
        while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(15); }
        progress.Close();
        task.GetAwaiter().GetResult();
        var index = studio.RequireIndex();
        var families = index.Families.ToArray();
        var disagreeing = studio.PlanSync().Count;
        log($"Clone index: {index.Entries.Count} textures, {families.Length} clone families ({families.Sum(f => f.Count)} textures), " +
            $"{index.Entries.Count(e => e.SignificantlyModified)} edited, {disagreeing} clone(s) behind a newer edit · {clock.Elapsed.TotalSeconds:0.0}s");
        foreach (var (archive, error) in index.Errors) log($"  could not scan {archive}: {error}");
        if (disagreeing > 0) log("  Texture › Sync clones copies the most recent edit of each family to its other members.");
        _textures.Refresh(_movie?.Selected);
    }

    private bool saveAll()
    {
        var studio = requireStudio();
        if (studio.Pending.IsEmpty) { log("Nothing to save."); return true; }
        pause();
        var report = studio.Save();
        foreach (var archive in report.Written) log($"Saved {archive} → {studio.Store.OutputPath(archive)}");
        foreach (var (archive, issue) in report.Issues.Where(i => i.Issue.Severity == IssueSeverity.Warning)) log($"  warning {archive}: {issue.Message}");
        foreach (var (archive, message) in report.Failed) log($"NOT saved {archive}: {message}");
        if (report.Failed.Count > 0)
        {
            MessageBox.Show(this, string.Join("\r\n\r\n", report.Failed.Select(f => $"{f.Archive}:\r\n{f.Message}")), "Some archives were not saved",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _explorer.RefreshMarkers();
        return true;
    }

    private void revertArchive(ArchiveId archive)
    {
        var studio = requireStudio();
        if (!Ui.Confirm(this, $"Delete the overlay copy of {archive}?\r\nThe game will use the original file again. Unsaved edits of this archive are discarded.")) return;
        studio.RevertArchive(archive);
        log($"Reverted {archive} to the original.");
        if (_movie?.Archive == archive) openMovie(archive, _movie.MovieName);
        _explorer.RefreshMarkers();
    }

    private void onStudioChanged()
    {
        if (InvokeRequired) { BeginInvoke(onStudioChanged); return; }
        _explorer.RefreshMarkers();
        _movie?.SyncWithSession();
        refreshMovie();
        updateTitle();
    }

    private void updateTitle()
    {
        var pending = _studio?.Pending;
        var count = pending is null ? 0 : pending.Archives.Count();
        Text = $"{(count > 0 ? "* " : "")}LumenStudio{(_studio is null ? "" : $" · {Path.GetFileNameWithoutExtension(_studio.Project.Path)}")}{(_movie is null ? "" : $" · {_movie.Title}")}";
        _pendingLabel.Text = pending is null ? "" : count == 0 ? "All saved" : $"{pending.Textures.Count} texture(s), {pending.Movies.Count} movie(s) unsaved in {count} archive(s)";
        _pendingLabel.ForeColor = count == 0 ? Theme.Muted : Theme.Pending;
    }

    // ───────────────────────────── movie ─────────────────────────────

    private string firstMovie(ArchiveId archive) =>
        Waddamburo.Formats.Ddp.DdpArchive.Open(requireStudio().ArchiveBytes(archive)).Index.Movies.First().Name;

    private void openMovie(ArchiveId archive, string movie)
    {
        var studio = requireStudio();
        pause();
        Cursor = Cursors.WaitCursor;
        try
        {
            closeMovie();
            _movie = new MovieController(studio, archive, movie) { RunScripts = _scriptsButton.Checked };
            _movie.FrameChanged += () => { };
            _stage.StageWidth = _movie.StageWidth; _stage.StageHeight = _movie.StageHeight;
            var root = studio.Project.GameRoot ?? PackedFolder.GameRootOf(studio.Project.BasePacked);
            if (root is not null && _movie.Document.Definition.Strings.Any(s => s.Value is "don1pM" or "don2pM")) _stage.BindDon(_movie.Player, root);
            _textures.SetMovie(_movie); _strings.SetMovie(_movie); _scripts.SetMovie(_movie);
            _stage.Fit();
            refreshMovie();
            if (_movie.RunScripts && !_movie.Snapshot.Quads.Any(q => q.MultiplyColor.Alpha > 0))
                log("The scene draws nothing with scripts on: its scripts wait for the game host. Turn 'Scripts on' off to see the authored layout.");
            log($"Opened {_movie.Title}: {_movie.Document.Definition.Sprites.Length} sprites, {_movie.Document.Definition.Shapes.Length} shapes, {_movie.TextureCount} textures, {_movie.FrameCount} frames at {_movie.FrameRate:0.#} fps{(_movie.Protected ? " · movie protected (textures only)" : "")}.");
            updateTitle();
        }
        finally { Cursor = Cursors.Default; }
    }

    private void closeMovie()
    {
        pause();
        _movie = null;
        _textures.SetMovie(null); _strings.SetMovie(null); _scripts.SetMovie(null); _text.Show(null, null);
        _properties.SelectedObject = null;
        _timeline.SetModel(TimelineModel.Build(new LmbMovieDefinition(null!, null, [], [], [], [], [], [], [], [], [], []), new LmbSpriteDefinition(0, 0, 1, 0, 0, [], [], null!)));
    }

    /// <summary>Rebuilds every movie-dependent view after an edit, undo or save.</summary>
    private void refreshMovie()
    {
        if (_movie is null) { refreshFrame(); return; }
        _refreshing = true;
        try
        {
            _timeline.Heading = _movie.Isolated ? $"{_movie.SpriteName(_movie.ViewRoot)} (entered)" : $"{_movie.SpriteName(_movie.ViewRoot)} (scene)";
            _timeline.SetModel(TimelineModel.Build(_movie.Document.Definition, _movie.ViewSprite));
            _strings.Reload();
            _scripts.Reload();
            _stage.CanTransform = !_movie.Protected;
        }
        finally { _refreshing = false; }
        refreshFrame();
        showSelection();
    }

    private void refreshFrame()
    {
        if (_movie is null)
        {
            _stage.SelectionBounds = null; _stage.HoverBounds = null; _stage.Invalidate();
            _frameLabel.Text = "";
            return;
        }
        _stage.SetFrame(_movie.Snapshot, _movie.Textures);
        _stage.SelectionBounds = _movie.SelectionBounds();
        _timeline.SetFrame(_movie.Frame);
        _frameLabel.Text = $"frame {_movie.Frame}/{_movie.FrameCount - 1} · {_movie.FrameRate:0.#} fps · {_stage.ZoomPercent:0}% · {_stage.RendererDescription}";
    }

    private void tick()
    {
        if (_movie is null || !_movie.Playing) return;
        _frameDebt += _playClock.Elapsed.TotalSeconds * _movie.FrameRate;
        _playClock.Restart();
        var frames = (int)Math.Min(30, Math.Floor(_frameDebt));
        if (frames <= 0) return;
        _frameDebt -= frames;
        _movie.Advance(frames);
        _stage.AdvanceNative(frames);
        refreshFrame();
    }

    private void togglePlay()
    {
        var movie = requireMovie();
        if (movie.Playing) { pause(); return; }
        movie.Play();
        _frameDebt = 0; _playClock.Restart(); _clock.Start();
        _playButton.Text = "⏸ Pause";
        _stage.HoverBounds = null;
    }

    private void pause()
    {
        _clock.Stop(); _playClock.Stop();
        _playButton.Text = "▶ Play";
        if (_movie is { Playing: true })
        {
            _movie.Pause();
            refreshFrame();
            showSelection();
        }
    }

    private void seek(int frame)
    {
        var movie = requireMovie();
        movie.Seek(Math.Clamp(frame, 0, movie.FrameCount - 1));
        refreshFrame();
        showSelection();
    }

    private void step(int delta)
    {
        var movie = requireMovie();
        pause();
        if (delta > 0) { movie.Advance(1); refreshFrame(); showSelection(); }
        else seek(movie.Frame - 1);
    }

    // ───────────────────────────── selection ─────────────────────────────

    private void select(Selection? selection)
    {
        if (_movie is null) return;
        _movie.Selected = selection;
        _stage.SelectionBounds = _movie.SelectionBounds();
        _stage.Invalidate();
        showSelection();
        if (selection?.Hit is { Kind: Waddamburo.Lumen.Rendering.LumenQuadOriginKind.Text }) _inspector.SelectedIndex = 2;
        else if (selection is not null && _inspector.SelectedIndex is 2 or 3 or 4) _inspector.SelectedIndex = 0;
    }

    private void selectDepth(ushort depth)
    {
        var movie = requireMovie();
        // A track maps to the direct child of the viewed timeline at that depth.
        foreach (var quad in movie.Snapshot.Quads)
            if (quad.Origin is { } origin && origin.Path.Length > 1 && origin.Path[1].AuthoredDepth == depth)
            {
                select(new Selection(origin.Path, 1, origin));
                return;
            }
        var placement = movie.Document.Placements(movie.ViewRoot, Math.Clamp(movie.Frame, 0, movie.FrameCount - 1)).FirstOrDefault(p => p.Depth == depth);
        if (placement is null) { select(null); _timeline.SelectedDepth = depth; _timeline.Invalidate(); return; }
        var name = movie.StringAt(placement.NameStringIndex) ?? "";
        var rootRef = new Waddamburo.Lumen.Rendering.LumenInstanceRef(movie.ViewRoot, "", 0, uint.MaxValue, movie.Frame, LumenMatrix.Identity);
        var childRef = new Waddamburo.Lumen.Rendering.LumenInstanceRef(placement.CharacterId, placement.NameStringIndex == 0 ? "" : name, Waddamburo.Lumen.Rendering.LumenInstanceRef.TimelineDepthBase + depth, placement.PlacementId, 0, LumenMatrix.Identity);
        select(new Selection([rootRef, childRef], 1, null));
    }

    private void showSelection()
    {
        if (_movie is null) return;
        var selection = _movie.Selected;
        _refreshing = true;
        try
        {
            buildBreadcrumb(selection);
            _timeline.SelectedDepth = selection is { Level: 1 } ? selection.Node.AuthoredDepth : null;
            _timeline.Invalidate();
            var placement = _movie.Placement();
            if (placement is null)
            {
                _properties.SelectedObject = null;
                _elementInfo.Text = selection is null ? "Click an element on the stage (pauses playback)." : "This element is created at runtime; it has no timeline placement.";
            }
            else
            {
                var bounds = _movie.SelectionBounds();
                var local = localSize(placement);
                _properties.SelectedObject = new ElementProperties(placement.Placement, _movie.Document, local.Width, local.Height);
                var nested = placement.Sprite != _movie.ViewRoot ? $" · in sprite {placement.Sprite} (frame {placement.Frame})" : "";
                _elementInfo.Text = $"{_movie.Describe(selection!.Node)} · depth {placement.Placement.Depth}{nested}" +
                    (bounds is { } b ? $"\r\nStage {b.X:0.#}, {b.Y:0.#} · {b.Width:0.#}×{b.Height:0.#}" : "");
            }
            _textures.Refresh(selection);
            _text.Show(_movie, selection);
        }
        finally { _refreshing = false; }
    }

    private SizeF localSize(SelectedPlacement placement)
    {
        // Size of the element's own geometry before its placement transform.
        var matrix = placement.Placement;
        var bounds = _movie!.SelectionBounds();
        if (bounds is not { } b) return new SizeF(1, 1);
        var m = _movie.Document.Matrix(matrix);
        var parent = placement.ParentWorld;
        var sx = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12) * MathF.Sqrt(parent.M11 * parent.M11 + parent.M12 * parent.M12);
        var sy = MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22) * MathF.Sqrt(parent.M21 * parent.M21 + parent.M22 * parent.M22);
        return new SizeF(b.Width / Math.Max(1e-4f, sx), b.Height / Math.Max(1e-4f, sy));
    }

    private void buildBreadcrumb(Selection? selection)
    {
        _breadcrumb.SuspendLayout();
        _breadcrumb.Controls.Clear();
        if (_movie is not null)
        {
            void crumb(string text, Action action, bool current)
            {
                var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = current ? Theme.Foreground : Theme.Highlight, ActiveLinkColor = Theme.Foreground, Margin = new Padding(2, 3, 2, 0) };
                link.LinkClicked += (_, _) => Ui.Guard(this, action);
                _breadcrumb.Controls.Add(link);
            }
            void arrow() => _breadcrumb.Controls.Add(new Label { Text = "›", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 3, 0, 0) });
            if (_movie.Isolated) { crumb("⌂ Scene", () => { _movie.ExitToScene(); refreshMovie(); }, false); arrow(); }
            crumb(_movie.SpriteName(_movie.ViewRoot), () => select(null), selection is null);
            if (selection is not null)
                for (var level = 1; level < selection.Path.Length; level++)
                {
                    var l = level;
                    arrow();
                    crumb(_movie.Describe(selection.Path[level]), () => select(selection.WithLevel(l)), level == selection.Level);
                }
        }
        _breadcrumb.ResumeLayout();
    }

    private void enterSelection()
    {
        var movie = requireMovie();
        if (movie.Selected is not { } selection) return;
        var id = selection.Node.CharacterId;
        if (!movie.Document.Definition.Sprites.Any(s => s.CharacterId == id && s.DeclaredFrameCount > 0))
        {
            // Entering a shape means entering its parent sprite.
            id = selection.Parent.CharacterId;
            if (id == movie.ViewRoot) return;
        }
        pause();
        movie.Enter(id);
        refreshMovie();
    }

    // ───────────────────────────── element edits ─────────────────────────────

    private SelectedPlacement requirePlacement() =>
        requireMovie().Placement() ?? throw new InvalidOperationException("Select an element placed by a timeline first.");

    private string? nameOf(LmbPlaceObjectCommand place) => place.NameStringIndex == 0 ? null : _movie!.StringAt(place.NameStringIndex);

    private void moveSelection(float dx, float dy, bool wholeTrack)
    {
        var movie = requireMovie();
        var target = requirePlacement();
        var (lx, ly) = toParent(target.ParentWorld, dx, dy);
        var depth = target.Placement.Depth;
        if (wholeTrack)
            movie.Edit(d => d.MapPlacementMatrices(target.Sprite, (_, place, matrix) => place.Depth == depth ? matrix with { X = matrix.X + lx, Y = matrix.Y + ly } : matrix));
        else
        {
            var m = movie.Document.Matrix(target.Placement);
            var place = target.Placement;
            movie.Edit(d => d.SetPlacement(target.Sprite, target.Frame, place.Depth, place.CharacterId, m with { X = m.X + lx, Y = m.Y + ly },
                d.Opacity(place), nameOf(place), place.BlendMode, place.ClipDepth));
        }
        log($"Moved {movie.Describe(movie.Selected!.Node)} by {lx:0.##}, {ly:0.##}{(wholeTrack ? " on every key" : $" at frame {target.Frame}")}.");
    }

    private static (float X, float Y) toParent(LumenMatrix world, float dx, float dy)
    {
        var determinant = world.M11 * world.M22 - world.M21 * world.M12;
        if (MathF.Abs(determinant) < 1e-8f) return (dx, dy);
        return ((world.M22 * dx - world.M21 * dy) / determinant, (-world.M12 * dx + world.M11 * dy) / determinant);
    }

    private void resizeSelection(float sx, float sy, PointF anchorStage)
    {
        var movie = requireMovie();
        var target = requirePlacement();
        var world = target.ParentWorld;
        var determinant = world.M11 * world.M22 - world.M21 * world.M12;
        var ax = anchorStage.X - world.X; var ay = anchorStage.Y - world.Y;
        var anchor = MathF.Abs(determinant) < 1e-8f ? (X: anchorStage.X, Y: anchorStage.Y)
            : (X: (world.M22 * ax - world.M21 * ay) / determinant, Y: (-world.M12 * ax + world.M11 * ay) / determinant);
        var m = movie.Document.Matrix(target.Placement);
        var changed = m with
        {
            M11 = m.M11 * sx, M21 = m.M21 * sx, M12 = m.M12 * sy, M22 = m.M22 * sy,
            X = anchor.X + (m.X - anchor.X) * sx, Y = anchor.Y + (m.Y - anchor.Y) * sy,
        };
        var place = target.Placement;
        movie.Edit(d => d.SetPlacement(target.Sprite, target.Frame, place.Depth, place.CharacterId, changed, d.Opacity(place), nameOf(place), place.BlendMode, place.ClipDepth));
    }

    private void applyProperties()
    {
        if (_refreshing || _properties.SelectedObject is not ElementProperties model) return;
        var movie = requireMovie();
        var target = requirePlacement();
        movie.Edit(d => d.SetPlacement(target.Sprite, target.Frame, model.Depth, model.CharacterId, model.Matrix, model.Alpha,
            string.IsNullOrEmpty(model.Name) ? null : model.Name, model.Blend, model.ClipDepth));
    }

    private void duplicate()
    {
        var movie = requireMovie();
        var target = requirePlacement();
        var place = target.Placement;
        var used = movie.Document.Definition.Sprites.Single(s => s.CharacterId == target.Sprite).Timeline.OfType<LmbPlaceObjectCommand>().Select(p => p.Depth).ToHashSet();
        var depth = (ushort)Enumerable.Range(1, ushort.MaxValue - 1).First(d => !used.Contains((ushort)d));
        var m = movie.Document.Matrix(place);
        movie.Edit(d => d.SetPlacement(target.Sprite, target.Frame, depth, place.CharacterId, m with { X = m.X + 16, Y = m.Y + 16 },
            d.Opacity(place), $"{nameOf(place) ?? "element"}_copy_{depth}", place.BlendMode, place.ClipDepth));
    }

    private void toggleVisibility()
    {
        var movie = requireMovie();
        var target = requirePlacement();
        var place = target.Placement;
        movie.Edit(d => d.SetPlacement(target.Sprite, target.Frame, place.Depth, place.CharacterId, d.Matrix(place), d.Opacity(place) > 0 ? 0 : 1, nameOf(place), place.BlendMode, place.ClipDepth));
    }

    private void removeElement()
    {
        var movie = requireMovie();
        var target = requirePlacement();
        movie.Edit(d => d.RemovePlacement(target.Sprite, target.Frame, target.Placement.Depth));
        select(null);
    }

    // ───────────────────────────── timeline edits ─────────────────────────────

    private ushort requireTrack()
    {
        var movie = requireMovie();
        if (_timeline.SelectedDepth is ushort depth) return depth;
        if (movie.Placement() is { } target && target.Sprite == movie.ViewRoot) return target.Placement.Depth;
        throw new InvalidOperationException("Select a track (or an element of the viewed timeline). To animate a nested element, enter its sprite (Ctrl+Enter).");
    }

    private void insertKeyframe()
    {
        var movie = requireMovie();
        var depth = requireTrack();
        var frame = movie.Frame;
        var place = movie.Document.Placements(movie.ViewRoot, frame).FirstOrDefault(p => p.Depth == depth)
            ?? throw new InvalidOperationException("The track has no element at this frame.");
        movie.Edit(d => d.SetPlacement(movie.ViewRoot, frame, depth, place.CharacterId, d.Matrix(place), d.Opacity(place), nameOf(place), place.BlendMode, place.ClipDepth));
    }

    private void deleteKeyframe()
    {
        var movie = requireMovie();
        var depth = requireTrack();
        movie.Edit(d => d.RemoveKeyframe(movie.ViewRoot, movie.Frame, depth));
    }

    private void tween()
    {
        var movie = requireMovie();
        var depth = requireTrack();
        var frame = movie.Frame;
        var place = movie.Document.Placements(movie.ViewRoot, frame).FirstOrDefault(p => p.Depth == depth)
            ?? throw new InvalidOperationException("The track has no element at this frame.");
        using var dialog = new Form { Text = "Create animation · transform at the final frame", Width = 470, Height = 660, StartPosition = FormStartPosition.CenterParent };
        Theme.BeginScaling(dialog);
        var endpoint = new AnimationProperties(new ElementProperties(place, movie.Document));
        var grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = endpoint, ToolbarVisible = false };
        var end = new NumericUpDown { Minimum = frame + 1, Maximum = Math.Max(frame + 1, movie.FrameCount - 1), Value = Math.Min(frame + 30, Math.Max(frame + 1, movie.FrameCount - 1)), Width = 90 };
        var smooth = new CheckBox { Text = "Ease in/out", Checked = true, AutoSize = true };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        top.Controls.AddRange([new Label { Text = "Final frame", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, end, smooth]);
        var ok = new Button { Text = "Create keyframes", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK, Height = 34 };
        dialog.Controls.Add(grid); dialog.Controls.Add(top); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        Theme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (end.Value >= movie.FrameCount) throw new InvalidOperationException("Extend the timeline first (Timeline › Extend timeline).");
        movie.Edit(d => d.Tween(movie.ViewRoot, depth, frame, (int)end.Value, endpoint.Matrix, endpoint.Alpha, smooth.Checked));
    }

    private void addLabel()
    {
        var movie = requireMovie();
        var name = Ui.Prompt(this, "Frame label", $"Label at frame {movie.Frame} of {movie.SpriteName(movie.ViewRoot)}:", "label");
        if (!string.IsNullOrWhiteSpace(name)) movie.Edit(d => d.AddLabel(movie.ViewRoot, movie.Frame, name.Trim()));
    }

    private void extendTimeline()
    {
        var movie = requireMovie();
        var value = Ui.Prompt(this, "Extend timeline", $"New total frame count for {movie.SpriteName(movie.ViewRoot)} (now {movie.FrameCount}):", (movie.FrameCount + 60).ToString(CultureInfo.InvariantCulture));
        if (value is not null) movie.Edit(d => d.ExtendTimeline(movie.ViewRoot, int.Parse(value, CultureInfo.InvariantCulture)));
    }

    private void timelineMenu(ushort? depth, Point location)
    {
        if (_movie is null) return;
        var menu = new ContextMenuStrip();
        void add(string text, Action action, bool enabled = true) { var item = menu.Items.Add(text, null, (_, _) => Ui.Guard(this, action)); item.Enabled = enabled; }
        add($"Insert keyframe at {_movie.Frame}", insertKeyframe, depth is not null);
        add("Delete keyframe", deleteKeyframe, depth is not null);
        add("Create animation…", tween, depth is not null);
        menu.Items.Add(new ToolStripSeparator());
        add("Add frame label…", addLabel);
        add("Add frame script…", () => openScripts(null, newScript: true));
        add("Extend timeline…", extendTimeline);
        menu.Items.Add(new ToolStripSeparator());
        add("Remove element from this frame", removeElement, depth is not null);
        Theme.Apply(menu);
        menu.Show(_timeline, location);
    }

    private void openScripts(int? action, bool newScript = false)
    {
        var movie = requireMovie();
        pause();
        using var editor = new ScriptEditorForm(movie, movie.ViewRoot, movie.Frame, action);
        if (newScript) editor.Shown += (_, _) => { };
        editor.ShowDialog(this);
        refreshMovie();
    }

    // ───────────────────────────── textures ─────────────────────────────

    private TextureId requireTexture() =>
        _textures.SelectedTexture ?? throw new InvalidOperationException("Select an element that draws a texture (or pick one in the Texture tab).");

    private string textureName(TextureId id) => TextureCommands.Name(requireStudio(), id);

    private void editTexture()
    {
        var id = requireTexture();
        pause();
        TextureCommands.Edit(this, requireStudio(), id, log);
    }

    private void applyTexture(TextureId id, RgbaImage image, bool propagate) => TextureCommands.Apply(requireStudio(), id, image, propagate, log);

    /// <summary>Test hook: edits the selected texture with a text layer and applies it with clone propagation.</summary>
    private void demoTextureEdit(string text, string? capture)
    {
        var studio = requireStudio();
        var id = requireTexture();
        var image = studio.TextureImage(id);
        using var editor = new TextureEditorForm(image, textureName(id), studio.PropagationTargets(id, Propagation.Clones).Count);
        editor.Show(this);
        editor.PlaceText(text, image.Width / 2f, image.Height / 2f);
        Application.DoEvents();
        if (capture is not null)
        {
            using var shot = new Bitmap(editor.Width, editor.Height);
            editor.DrawToBitmap(shot, new Rectangle(0, 0, editor.Width, editor.Height));
            shot.Save(capture);
        }
        editor.Commit();
        var result = editor.Result;
        editor.DialogResult = DialogResult.OK;
        editor.Close();
        applyTexture(id, result, propagate: true);
    }

    private void replaceTexture() => TextureCommands.Replace(this, requireStudio(), requireTexture(), log);

    private void exportTexture() => TextureCommands.Export(this, requireStudio(), requireTexture());

    private void revertTexture() => TextureCommands.Revert(this, requireStudio(), requireTexture(), log);

    private IReadOnlyList<TextureId> movieTextures()
    {
        var movie = requireMovie();
        return Enumerable.Range(0, movie.TextureCount).Select(movie.GlobalTexture).ToArray();
    }

    private void exportAll() => TextureCommands.ExportAll(this, requireStudio(), movieTextures(), log);

    private void importFolder() => TextureCommands.ImportFolder(this, requireStudio(), movieTextures(), log);

    private void syncClones()
    {
        var studio = requireStudio();
        scanClones(force: false);
        var plan = studio.PlanSync();
        if (plan.Count == 0) { MessageBox.Show(this, "Every clone family already shows its most recent edit.", "Sync clones", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dialog = new SyncDialog(studio, plan);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var selected = dialog.Selected.ToArray();
        studio.ApplySync(selected);
        log($"Queued {selected.Length} clone update(s) in {selected.Select(a => a.Target.Id.Archive).Distinct().Count()} archive(s). Save all to write them.");
    }

    private void showClones()
    {
        var studio = requireStudio();
        scanClones(force: false);
        TextureCommands.ShowClones(this, studio, requireTexture());
        _textures.Refresh(_movie?.Selected);
    }

    // ───────────────────────────── tools ─────────────────────────────

    private void validate()
    {
        var studio = requireStudio();
        if (!studio.Pending.IsEmpty) log("Note: validation checks saved archives; unsaved edits are validated when saving.");
        Cursor = Cursors.WaitCursor;
        string report;
        try { report = Validation.Report(studio); }
        finally { Cursor = Cursors.Default; }
        new ReportForm("Output validation", report).ShowDialog(this);
    }

    private string requireExecutable()
    {
        var studio = requireStudio();
        if (WaddamburoLauncher.FindExecutable(studio.Project, _settings) is { } exe) return exe;
        using var dialog = new OpenFileDialog { Title = "Locate Waddamburo (Waddamburo.exe or Waddamburo.App.exe)", Filter = "Waddamburo|*.exe" };
        if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException("No Waddamburo executable selected.");
        _settings.WaddamburoExecutable = dialog.FileName; _settings.Save();
        return dialog.FileName;
    }

    private void testMovie(bool layoutOnly)
    {
        var movie = requireMovie();
        var studio = requireStudio();
        var exe = requireExecutable();
        var bytes = studio.Pending.Archives.Contains(movie.Archive) ? ArchiveBuilder.Build(studio.Store, movie.Archive, studio.Pending) : studio.ArchiveBytes(movie.Archive);
        WaddamburoLauncher.TestMovie(exe, bytes, movie.MovieName, layoutOnly);
        log($"Started Waddamburo on {movie.Title}{(layoutOnly ? " (layout only)" : "")}.");
    }

    private void testGame()
    {
        var studio = requireStudio();
        if (!studio.Pending.IsEmpty)
        {
            if (!Ui.Confirm(this, "The full-game preview uses saved archives. Save all now?")) return;
            if (!saveAll()) return;
        }
        var exe = requireExecutable();
        var root = WaddamburoLauncher.TestGame(exe, studio.Project);
        log($"Started Waddamburo on the preview data {root} (junctions to the original data plus the overlay).");
    }

    private void log(string text)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
    }

    private void captureWindow(string path)
    {
        using var shot = new Bitmap(Width, Height);
        DrawToBitmap(shot, new Rectangle(0, 0, Width, Height));
        using (var stage = _stage.CaptureImage())
            if (stage is not null)
            {
                using var g = Graphics.FromImage(shot);
                var origin = _stage.PointToScreen(Point.Empty);
                g.DrawImageUnscaled(stage, origin.X - Left, origin.Y - Top);
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        shot.Save(path);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var typing = ActiveControl is TextBoxBase || _properties.ContainsFocus || ActiveControl is ComboBox || ActiveControl is NumericUpDown;
        if (!typing)
        {
            switch (keyData)
            {
                case Keys.Space: Ui.Guard(this, togglePlay); return true;
                case Keys.Oemcomma: Ui.Guard(this, () => step(-1)); return true;
                case Keys.OemPeriod: Ui.Guard(this, () => step(1)); return true;
                case Keys.Escape: select(null); return true;
                case Keys.Home: Ui.Guard(this, () => { pause(); seek(0); }); return true;
                case Keys.End: Ui.Guard(this, () => { pause(); seek(int.MaxValue); }); return true;
                case Keys.Delete when _movie?.Selected is not null: Ui.Guard(this, removeElement); return true;
                case Keys.Left or Keys.Right or Keys.Up or Keys.Down or (Keys.Shift | Keys.Left) or (Keys.Shift | Keys.Right) or (Keys.Shift | Keys.Up) or (Keys.Shift | Keys.Down)
                    when _stage.Focused && _movie?.Selected is not null:
                    var amount = keyData.HasFlag(Keys.Shift) ? 10 : 1;
                    var key = keyData & Keys.KeyCode;
                    Ui.Guard(this, () => moveSelection(key == Keys.Left ? -amount : key == Keys.Right ? amount : 0, key == Keys.Up ? -amount : key == Keys.Down ? amount : 0, _wholeTrackButton.Checked));
                    return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _clock.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A small modal-looking progress window for long scans.</summary>
internal sealed class ProgressForm : Form
{
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22 };
    private readonly Label _label = new() { Dock = DockStyle.Fill, Padding = new Padding(4) };

    public ProgressForm(string title)
    {
        Theme.BeginScaling(this);
        Text = title; Width = 520; Height = 120; FormBorderStyle = FormBorderStyle.FixedToolWindow; ControlBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        Controls.Add(_label); Controls.Add(_bar);
        Theme.Apply(this);
    }

    public void Report(int done, int total, string current)
    {
        if (IsDisposed) return;
        _bar.Maximum = Math.Max(1, total); _bar.Value = Math.Clamp(done, 0, _bar.Maximum);
        _label.Text = $"{done}/{total} · {current}";
    }
}

internal static class Help
{
    public const string Text = """
        STAGE
          Click ............................ select the element under the cursor (pauses playback)
          Ctrl+click ....................... select the innermost element (the shape that drew the pixel)
          Double-click ..................... enter a sprite (edit its own timeline) / edit a texture
          Drag the selection ............... move it (toolbar: whole track or current key only); Shift locks an axis
          Drag the corner handle ........... resize; Shift keeps proportions
          Arrows / Shift+arrows ............ nudge 1 / 10 pixels
          Wheel ............................ zoom at the cursor; right or middle drag pans
          Alt+Up / Alt+Down ................ select the parent / child element
          Esc .............................. deselect;  Ctrl+Backspace returns from an entered sprite

        PLAYBACK AND TIMELINE
          Space ............................ play / pause;   , and . step one frame;  Home / End
          Click or drag in the timeline .... scrub;  click a track to select its element
          Ctrl+wheel ....................... zoom the timeline;  Shift+wheel scrolls horizontally
          F6 / Shift+F6 .................... insert / delete keyframe;  Ctrl+T creates an animation
          Right-click a track .............. keyframes, labels, scripts, duration

        TEXTURES
          Ctrl+E ........................... edit the selected texture (text, layers, erase, fill, size)
          Ctrl+R ........................... replace it with an image of any size (it keeps its on-screen size)
          Ctrl+Shift+Y ..................... sync clones: the most recent edit of each identical image wins
          Clone propagation copies an edit to every texture with the same original image, in every archive
          and linked layer. Blank/solid images and unlinked textures are skipped.

        FILES
          Ctrl+S ........................... write modified archives to the overlay output (only those)
          F5 / Ctrl+F5 ..................... test the movie / the full game in Waddamburo
          Original game files are never written. Validate output checks the original hardware's invariants.
        """;
}
