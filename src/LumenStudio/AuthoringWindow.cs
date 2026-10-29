using System.ComponentModel;
using System.Diagnostics;
using LumenStudio.Authoring.Packaging;
using LumenStudio.Authoring.Preview;
using LumenStudio.Authoring.Project;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace LumenStudio;

/// <summary>
/// Lumen Author: movies made from scratch. The project (*.lumen.json) is a tree of symbols edited in the
/// property grid, scripts are LumenScript files edited on the right; F5 compiles the movie and plays it
/// on the stage with a stand-in Wd catalog (Left/A, Right/S, Space/Z drive it like the drum), F6 builds
/// it over a mirror of the game and starts Waddamburo on Song Select.
/// </summary>
internal sealed class AuthoringWindow : Form
{
    private readonly AppSettings _settings;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None };
    private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
    private readonly StageView _stage = new() { Dock = DockStyle.Fill, TabStop = true };
    private readonly TextBox _script = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsTab = true, Font = Theme.MonoFont };
    private readonly Label _scriptCaption = Ui.Caption("Select a script in the tree.");
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.MonoFont };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly HashSet<int> _keys = [];

    private string? _path;
    private LumenProject? _project;
    private bool _dirty;
    private (SymbolSource Symbol, string Frame)? _scriptOwner;
    private bool _scriptDirty;
    private LumenPlayer? _player;
    private IReadOnlyList<LumenSoftwareTexture> _textures = [];
    private PreviewSongSelectHost? _host;
    private int _hostLogged;
    private bool _playing = true;

    public AuthoringWindow(AppSettings settings, string? path = null)
    {
        _settings = settings;
        Text = "Lumen Author";
        Width = 1500; Height = 920;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Theme.BeginScaling(this);

        var menu = new MenuStrip();
        ToolStripMenuItem root(string title) { var item = new ToolStripMenuItem(title); menu.Items.Add(item); return item; }
        void item(ToolStripMenuItem parent, string text, Keys keys, Action action) =>
            parent.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => Ui.Guard(this, action)) { ShortcutKeys = keys });
        var file = root("&File");
        item(file, "New Lumen movie…", Keys.Control | Keys.N, newProject);
        item(file, "Open Lumen movie…", Keys.Control | Keys.O, () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Lumen movie (*.lumen.json)|*.lumen.json|JSON|*.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK) open(dialog.FileName);
        });
        item(file, "Save", Keys.Control | Keys.S, save);
        item(file, "Game folder (USRDIR)…", Keys.None, chooseGame);
        item(file, "Close", Keys.Control | Keys.W, Close);
        var insert = root("&Insert");
        item(insert, "Symbol from a game archive…", Keys.Control | Keys.I, importSymbol);
        item(insert, "Image (PNG)…", Keys.None, addImage);
        item(insert, "Rectangle", Keys.None, () => addSymbol(new SymbolSource { Id = uniqueId("Rect"), Kind = "rect", Width = 200, Height = 100, Color = "#FFFFFF" }));
        item(insert, "Text field", Keys.None, () => addSymbol(new SymbolSource { Id = uniqueId("Text"), Kind = "text", Width = 400, Height = 40, Size = 24, Color = "#FFFFFF" }));
        item(insert, "Sprite", Keys.None, () => addSymbol(new SymbolSource { Id = uniqueId("Sprite"), Kind = "sprite", Layers = [] }));
        item(insert, "Layer in the selected sprite…", Keys.Control | Keys.L, addLayer);
        item(insert, "Frame script in the selected sprite…", Keys.None, addScript);
        var build = root("&Build");
        item(build, "Build and preview", Keys.F5, () => _ = buildAndPreview());
        item(build, "Run in Waddamburo (Song Select)", Keys.F6, () => _ = runInGame());
        item(build, "Export archive…", Keys.None, () => _ = exportArchive());
        var preview = root("&Preview");
        item(preview, "Play / pause", Keys.F8, () => _playing = !_playing);
        item(preview, "Restart", Keys.Control | Keys.R, restart);
        item(preview, "Fit stage", Keys.Control | Keys.D0, _stage.Fit);

        var left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        left.Panel1.Controls.Add(_tree);
        left.Panel2.Controls.Add(_grid);
        var scriptPanel = new Panel { Dock = DockStyle.Fill };
        scriptPanel.Controls.Add(_script);
        scriptPanel.Controls.Add(_scriptCaption);
        _scriptCaption.Dock = DockStyle.Top;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var scriptTab = new TabPage("Script"); scriptTab.Controls.Add(scriptPanel);
        var logTab = new TabPage("Build log"); logTab.Controls.Add(_log);
        tabs.TabPages.Add(scriptTab); tabs.TabPages.Add(logTab);
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        right.Panel1.Controls.Add(_stage);
        right.Panel2.Controls.Add(tabs);
        var main = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(left);
        main.Panel2.Controls.Add(right);
        Controls.Add(main);
        Controls.Add(_status);
        Controls.Add(menu);
        MainMenuStrip = menu;
        Theme.Apply(this);
        // Splitters are placed once the window has its size (set earlier they are clamped to defaults).
        Load += (_, _) =>
        {
            main.SplitterDistance = Theme.Scale(this, 380);
            left.SplitterDistance = left.Height * 55 / 100;
            right.SplitterDistance = Math.Max(100, right.Height - Theme.Scale(this, 250));
        };

        _tree.AfterSelect += (_, e) => select(e.Node?.Tag);
        _grid.PropertyValueChanged += (_, _) => { _dirty = true; refreshTree(); };
        _script.TextChanged += (_, _) => _scriptDirty = _scriptOwner is not null;
        _stage.MouseDown += (_, _) => _stage.Focus();
        // A key released elsewhere would stay held in the preview (an endless Ka).
        _stage.LostFocus += (_, _) => _keys.Clear();
        Deactivate += (_, _) => _keys.Clear();
        _timer.Tick += (_, _) => tick();
        FormClosing += (_, e) => { if (!confirmDiscard()) e.Cancel = true; else _timer.Stop(); };
        Shown += (_, _) =>
        {
            if (path is not null) Ui.Guard(this, () => open(path));
            _timer.Start();
        };
    }

    /// <summary>Headless check: build, play <paramref name="frames"/> frames with optional key presses, save a window shot, close.</summary>
    public void CaptureAfter(string capture, int frames, IReadOnlyDictionary<int, int[]> keys)
    {
        Shown += async (_, _) =>
        {
            // The build runs in the background: wait for its player (or its failure).
            for (var waited = 0; _player is null && waited < 1200; waited++) await Task.Delay(100);
            _timer.Stop();
            var clock = Stopwatch.StartNew();
            var slowest = 0.0;
            for (var frame = 1; frame <= frames && _player is not null; frame++)
            {
                _keys.Clear();
                foreach (var code in keys.GetValueOrDefault(frame) ?? []) _keys.Add(code);
                _playing = true;
                var at = clock.Elapsed.TotalMilliseconds;
                tick();
                slowest = Math.Max(slowest, clock.Elapsed.TotalMilliseconds - at);
            }
            log($"Preview: {frames} frames in {clock.ElapsedMilliseconds} ms (slowest {slowest:0} ms) on the {_stage.RendererDescription} renderer.");
            Application.DoEvents();
            File.WriteAllText(Path.ChangeExtension(capture, ".log"), _log.Text);
            using var shot = new Bitmap(Width, Height);
            DrawToBitmap(shot, new Rectangle(0, 0, Width, Height));
            using (var stage = _stage.CaptureImage())
                if (stage is not null)
                {
                    using var g = Graphics.FromImage(shot);
                    var origin = _stage.PointToScreen(Point.Empty);
                    g.DrawImageUnscaled(stage, origin.X - Left, origin.Y - Top);
                }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
            shot.Save(capture);
            _dirty = false; _scriptDirty = false;
            Close();
        };
        updateStatus();
    }

    // --- Project ---

    /// <summary>A game folder for this session only (--game=), not saved in the settings.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string? GameRootOverride { get; init; }

    private string? gameRoot => GameRootOverride ?? _settings.AuthoringGameRoot;

    private void newProject()
    {
        if (!confirmDiscard()) return;
        using var dialog = new SaveFileDialog { Filter = "Lumen movie (*.lumen.json)|*.lumen.json", FileName = "movie.lumen.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(dialog.FileName));
        var project = new LumenProject
        {
            Archive = $"{name}/packeddata.ddp",
            Movie = $"{name}/{name}.lm",
            Root = "Main",
            Symbols =
            [
                new SymbolSource { Id = "Backdrop", Kind = "rect", Width = 1280, Height = 720, Color = "#1E2A44" },
                new SymbolSource { Id = "Hello", Kind = "text", Width = 800, Height = 60, Size = 40, Align = "center", Color = "#FFFFFF", OriginX = 0.5f, OriginY = 0.5f },
                new SymbolSource
                {
                    Id = "Main", Kind = "sprite", Frames = 60,
                    Layers =
                    [
                        new LayerSource { Name = "backdrop", Symbol = "Backdrop" },
                        new LayerSource { Name = "hello", Symbol = "Hello", Keys =
                        [
                            new KeySource { Frame = 0, X = 640, Y = 300, Alpha = 0, Tween = "easeOut" },
                            new KeySource { Frame = 40, Y = 360, Alpha = 1 },
                        ] },
                    ],
                    Scripts = new() { ["0"] = "@main.as", ["59"] = "stop();" },
                },
            ],
        };
        var script = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "main.as");
        if (!File.Exists(script))
            File.WriteAllText(script, "// Frame 0 of Main. LumenScript: an ActionScript 2 subset.\nhello.text = \"Hello from LumenScript\";\n");
        project.Save(dialog.FileName);
        open(dialog.FileName);
    }

    private void open(string path)
    {
        if (!confirmDiscard()) return;
        _project = LumenProject.Load(path);
        _path = Path.GetFullPath(path);
        _dirty = false;
        _scriptOwner = null; _script.Text = ""; _scriptDirty = false;
        Text = $"Lumen Author — {Path.GetFileName(_path)}";
        refreshTree();
        log($"Opened {_path}");
        if (gameRoot is { } root) GameFonts.Load(Path.Combine(root, "data", "font", "font.ttf"));
        if (gameRoot is not null || _project.Imports.Count == 0) _ = buildAndPreview();
        else log("Set File › Game folder (USRDIR) to build: the project borrows the game's art.");
    }

    private void save()
    {
        var project = requireProject();
        commitScript();
        project.Save(_path!);
        _dirty = false;
        updateStatus();
    }

    private bool confirmDiscard()
    {
        if (_project is null || (!_dirty && !_scriptDirty)) return true;
        var answer = MessageBox.Show(this, "Save the changes to this movie?", "Lumen Author", MessageBoxButtons.YesNoCancel);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.Yes) save();
        return true;
    }

    private LumenProject requireProject() => _project ?? throw new InvalidOperationException("Open or create a Lumen movie first.");

    private void chooseGame()
    {
        using var dialog = new FolderBrowserDialog { Description = "The game's USRDIR (it has data/lumendata/packed)", SelectedPath = gameRoot ?? "" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!Directory.Exists(Path.Combine(dialog.SelectedPath, "data", "lumendata", "packed")))
            throw new InvalidOperationException("That folder has no data/lumendata/packed.");
        _settings.AuthoringGameRoot = dialog.SelectedPath;
        _settings.Save();
        GameFonts.Load(Path.Combine(dialog.SelectedPath, "data", "font", "font.ttf"));
        updateStatus();
    }

    // --- Tree, properties, scripts ---

    private void refreshTree()
    {
        var selected = _tree.SelectedNode?.Tag;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            if (_project is not { } project) return;
            var movie = _tree.Nodes.Add($"Movie  {project.Movie}  ({project.Width}×{project.Height}, {project.Fps} fps)");
            movie.Tag = project;
            var imports = _tree.Nodes.Add("Imports (game archives)");
            foreach (var import in project.Imports) imports.Nodes.Add(new TreeNode($"{import.Id}  {import.Movie}") { Tag = import });
            var textures = _tree.Nodes.Add("Images");
            foreach (var texture in project.Textures) textures.Nodes.Add(new TreeNode($"{texture.Id}  {texture.File}") { Tag = texture });
            var symbols = _tree.Nodes.Add("Symbols");
            foreach (var symbol in project.Symbols)
            {
                var node = symbols.Nodes.Add($"{symbol.Id}  [{symbol.Kind}{(symbol.From is { } from ? " " + from : "")}{(symbol.Export is { } export ? " export " + export : "")}]{(symbol.Id == project.Root ? "  ★ root" : "")}");
                node.Tag = symbol;
                foreach (var layer in symbol.Layers ?? [])
                    node.Nodes.Add(new TreeNode($"layer {layer.Name ?? "(unnamed)"} ← {layer.Symbol}  ({layer.Keys.Count} keys)") { Tag = layer });
                foreach (var (frame, code) in symbol.Scripts ?? [])
                    node.Nodes.Add(new TreeNode($"script frame {frame}{(code.StartsWith('@') ? "  " + code[1..] : "")}") { Tag = (symbol, frame) });
            }
            _tree.ExpandAll();
            foreach (var node in all(_tree.Nodes))
                if (node.Tag is not null && Equals(node.Tag, selected)) { _tree.SelectedNode = node; break; }
        }
        finally { _tree.EndUpdate(); }
        updateStatus();
    }

    private static IEnumerable<TreeNode> all(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in all(node.Nodes)) yield return child;
        }
    }

    private void select(object? tag)
    {
        if (tag is ValueTuple<SymbolSource, string> script)
        {
            commitScript();
            _grid.SelectedObject = null;
            loadScript(script.Item1, script.Item2);
            return;
        }
        _grid.SelectedObject = tag is LumenProject or ImportSource or TextureSource or SymbolSource or LayerSource ? tag : null;
    }

    private void loadScript(SymbolSource symbol, string frame)
    {
        var code = symbol.Scripts![frame];
        _scriptOwner = (symbol, frame);
        _script.Text = (code.StartsWith('@') ? File.ReadAllText(resolve(code[1..])) : code).ReplaceLineEndings("\r\n");
        _scriptCaption.Text = $"{symbol.Id} · frame {frame}{(code.StartsWith('@') ? " · " + code[1..] : " · inline")}   (Ctrl+S saves; F5 builds)";
        _scriptDirty = false;
    }

    private void commitScript()
    {
        if (_scriptOwner is not { } owner || !_scriptDirty) return;
        var code = owner.Symbol.Scripts![owner.Frame];
        var text = _script.Text.ReplaceLineEndings("\n");
        if (code.StartsWith('@')) File.WriteAllText(resolve(code[1..]), text);
        else { owner.Symbol.Scripts[owner.Frame] = text; _dirty = true; }
        _scriptDirty = false;
    }

    private string resolve(string relative) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_path!)!, relative));

    private string uniqueId(string stem)
    {
        var project = requireProject();
        for (var n = 1; ; n++)
            if (project.Symbols.All(s => s.Id != $"{stem}{n}")) return $"{stem}{n}";
    }

    private void addSymbol(SymbolSource symbol)
    {
        requireProject().Symbols.Insert(Math.Max(0, requireProject().Symbols.FindIndex(s => s.Id == requireProject().Root)), symbol);
        _dirty = true;
        refreshTree();
        selectTag(symbol);
    }

    private void selectTag(object tag)
    {
        foreach (var node in all(_tree.Nodes)) if (ReferenceEquals(node.Tag, tag)) { _tree.SelectedNode = node; return; }
    }

    private SymbolSource selectedSprite() =>
        (_tree.SelectedNode?.Tag switch
        {
            SymbolSource { Kind: "sprite" } sprite => sprite,
            LayerSource or ValueTuple<SymbolSource, string> => _tree.SelectedNode.Parent?.Tag as SymbolSource,
            _ => null,
        }) ?? requireProject().Symbols.FirstOrDefault(s => s.Id == requireProject().Root)
        ?? throw new InvalidOperationException("Select a sprite symbol first.");

    private void addLayer()
    {
        var sprite = selectedSprite();
        var project = requireProject();
        var choices = project.Symbols.Where(s => s != sprite).Select(s => s.Id).ToArray();
        var symbol = Ui.Prompt(this, "Add layer", $"Symbol to place in {sprite.Id} ({string.Join(", ", choices.Take(12))}{(choices.Length > 12 ? "…" : "")}; or importId:ExportName):", choices.FirstOrDefault() ?? "");
        if (string.IsNullOrWhiteSpace(symbol)) return;
        var layer = new LayerSource { Name = symbol.Split(':')[^1].ToLowerInvariant().Replace("[", "_").Replace("]", "", StringComparison.Ordinal), Symbol = symbol.Trim(), Keys = [new KeySource { Frame = 0, X = 0, Y = 0 }] };
        (sprite.Layers ??= []).Add(layer);
        _dirty = true;
        refreshTree();
        selectTag(layer);
    }

    private void addScript()
    {
        var sprite = selectedSprite();
        var frame = Ui.Prompt(this, "Frame script", $"Frame (0-based) of {sprite.Id}:", "0");
        if (frame is null || !int.TryParse(frame, out var number) || number < 0) return;
        sprite.Scripts ??= [];
        var key = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!sprite.Scripts.ContainsKey(key))
        {
            var file = $"{sprite.Id.ToLowerInvariant()}_{number}.as";
            if (!File.Exists(resolve(file))) File.WriteAllText(resolve(file), $"// {sprite.Id}, frame {number}\n");
            sprite.Scripts[key] = "@" + file;
            _dirty = true;
        }
        refreshTree();
        foreach (var node in all(_tree.Nodes))
            if (node.Tag is ValueTuple<SymbolSource, string> t && t.Item1 == sprite && t.Item2 == key) { _tree.SelectedNode = node; break; }
    }

    private void addImage()
    {
        var project = requireProject();
        using var dialog = new OpenFileDialog { Filter = "PNG image|*.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var folder = Path.GetDirectoryName(_path!)!;
        var relative = Path.GetRelativePath(folder, dialog.FileName);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.Combine(folder, "art"));
            relative = Path.Combine("art", Path.GetFileName(dialog.FileName));
            File.Copy(dialog.FileName, Path.Combine(folder, relative), overwrite: true);
        }
        var id = Path.GetFileNameWithoutExtension(dialog.FileName);
        if (project.Textures.All(t => t.Id != id)) project.Textures.Add(new TextureSource { Id = id, File = relative.Replace('\\', '/') });
        addSymbol(new SymbolSource { Id = uniqueId(id), Kind = "image", Texture = id });
    }

    /// <summary>Lists a game archive's exported sprites and adds the chosen ones as import symbols.</summary>
    private void importSymbol()
    {
        var project = requireProject();
        var root = gameRoot ?? throw new InvalidOperationException("Set File › Game folder (USRDIR) first.");
        var packed = Path.Combine(root, "data", "lumendata", "packed");
        using var dialog = new OpenFileDialog { Filter = "Lumen archive|packeddata.ddp;*.ddp", InitialDirectory = packed };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var archive = DdpArchive.Open(File.ReadAllBytes(dialog.FileName));
        var movieEntry = archive.Index.Movies[0];
        var movie = LmbSemanticReader.Read(LmbFile.Parse(archive.OpenMovie(movieEntry).Data)).Value;
        var exports = movie.Sprites
            .Select(s => (Sprite: s, Name: s.ExportStringIndex is uint i && i < movie.Strings.Length ? movie.Strings[(int)i].Value : null))
            .Where(e => e.Name is not null && !e.Name.StartsWith("__Packages.", StringComparison.Ordinal))
            .Select(e => $"{e.Name}   ({e.Sprite.DeclaredFrameCount} frames)").ToArray();
        using var picker = new Form { Text = $"Import from {movieEntry.Name}", Width = 520, Height = 640, StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, Font = Theme.MonoFont };
        list.Items.AddRange(exports);
        var ok = new Button { Text = "Add symbols", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        picker.Controls.Add(list); picker.Controls.Add(ok);
        picker.AcceptButton = ok;
        Theme.Apply(picker);
        if (picker.ShowDialog(this) != DialogResult.OK || list.SelectedItems.Count == 0) return;
        var relative = Path.GetRelativePath(root, dialog.FileName).Replace('\\', '/');
        var source = project.Imports.FirstOrDefault(i => i.Movie == movieEntry.Name);
        if (source is null)
        {
            source = new ImportSource { Id = Path.GetFileNameWithoutExtension(movieEntry.Name), Archive = "${game}/" + relative, Movie = movieEntry.Name };
            project.Imports.Add(source);
        }
        SymbolSource? last = null;
        foreach (string entry in list.SelectedItems)
        {
            var name = entry[..entry.IndexOf("   (", StringComparison.Ordinal)];
            var id = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            if (project.Symbols.Any(s => s.Id == id)) continue;
            last = new SymbolSource { Id = id, Kind = "import", From = $"{source.Id}:{name}" };
            project.Symbols.Insert(Math.Max(0, project.Symbols.FindIndex(s => s.Id == project.Root)), last);
        }
        _dirty = true;
        refreshTree();
        if (last is not null) selectTag(last);
        log($"Added {list.SelectedItems.Count} symbol(s) from {movieEntry.Name}; place them with Insert › Layer.");
    }

    // --- Build and preview ---

    private bool _busy;

    /// <summary>
    /// Compiles off the UI thread (reading the borrowed game archive and encoding textures takes seconds);
    /// null when the build failed (reported) or another one is running.
    /// </summary>
    private async Task<CompiledMovie?> compile()
    {
        if (_busy) { log("A build is already running."); return null; }
        // Saved only when edited here: a hand-written project keeps its comments and layout.
        if (_dirty || _scriptDirty) save();
        var path = _path ?? throw new InvalidOperationException("Open or create a Lumen movie first.");
        var root = gameRoot;
        _busy = true;
        UseWaitCursor = true;
        _status.Text = "  Building…";
        try
        {
            var clock = Stopwatch.StartNew();
            var built = await Task.Run(() => ProjectCompiler.Compile(path, root));
            foreach (var line in built.Log) log(line);
            log($"Built in {clock.ElapsedMilliseconds} ms.");
            return built;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log("ERROR " + error.Message);
            MessageBox.Show(this, error.Message, "Build failed");
            return null;
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            updateStatus();
        }
    }

    private async Task buildAndPreview()
    {
        if (await compile() is not { } built) return;
        var (movie, textures) = await Task.Run(() => PreviewMovie.Load(built.Archive, built.Project.Movie));
        _textures = textures;
        _host = new PreviewSongSelectHost();
        _hostLogged = 0;
        _diagnosticsLogged = 0;
        _keys.Clear();
        _player = PreviewMovie.Start(movie, _host);
        _stage.StageWidth = PreviewMovie.StageWidth(movie);
        _stage.StageHeight = PreviewMovie.StageHeight(movie);
        _playing = true;
        _stage.Focus();
        updateStatus();
    }

    private void restart() => _ = buildAndPreview();

    private void tick()
    {
        if (_player is null) return;
        if (_playing)
        {
            _player.Advance(new LumenInputSnapshot(_keys));
            foreach (var diagnostic in _player.Diagnostics.Skip(_diagnosticsLogged)) log($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            _diagnosticsLogged = _player.Diagnostics.Length;
        }
        if (_host is not null)
        {
            foreach (var line in _host.Log.Skip(_hostLogged)) log("Wd " + line);
            _hostLogged = _host.Log.Count;
        }
        _stage.SetFrame(_player.CreateRenderSnapshot(), _textures);
        _status.Text = $"  frame {_player.CurrentFrame}/{_player.FrameCount - 1}{(_playing ? "" : " (paused)")} · game: {gameRoot ?? "not set"} · keys: Left/A Ka, Right/S Ka, Space/Z Don (click the stage first)";
    }

    private int _diagnosticsLogged;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_stage.Focused && map(keyData & Keys.KeyCode) is int code)
        {
            _keys.Add(code);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (map(e.KeyCode) is int code) _keys.Remove(code);
        base.OnKeyUp(e);
    }

    // The drum as Waddamburo maps it: left Ka → A, right Ka → S, Don → Z (Flash key codes).
    private static int? map(Keys key) => key switch
    {
        Keys.Left or Keys.Up or Keys.A or Keys.D => 65,
        Keys.Right or Keys.Down or Keys.S or Keys.K => 83,
        Keys.Space or Keys.Enter or Keys.Z or Keys.F or Keys.J => 90,
        _ => null,
    };

    private async Task runInGame()
    {
        var root = gameRoot ?? throw new InvalidOperationException("Set File › Game folder (USRDIR) first.");
        var executable = WaddamburoLauncher.FindAuthoringExecutable(_settings)
            ?? throw new InvalidOperationException("No Waddamburo build with the Wd host API was found next to LumenStudio (waddamburo\\Waddamburo.App.exe).");
        if (await compile() is not { } built) return;
        var preview = Path.Combine(Path.GetDirectoryName(_path!)!, "build", "preview-usrdir");
        _status.Text = "  Preparing the preview game folder…";
        try
        {
            await Task.Run(() => PreviewRoot.Build(root, preview, new Dictionary<string, byte[]> { [built.Project.Archive] = built.Archive }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log("ERROR " + error.Message);
            MessageBox.Show(this, error.Message, "Preview folder failed");
            return;
        }
        log($"Preview USRDIR {preview} (the game folder is linked, never written).");
        var scene = built.Project.Archive.StartsWith("song_select/", StringComparison.Ordinal) ? " --start-scene=song-select" : "";
        Process.Start(new ProcessStartInfo(executable, $"--game-data=\"{preview}\"{scene}") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! })?.Dispose();
        log($"Started {executable} (its first start after an update can take a while).");
        updateStatus();
    }

    private async Task exportArchive()
    {
        if (await compile() is not { } built) return;
        using var dialog = new SaveFileDialog { Filter = "Lumen archive|*.ddp", FileName = Path.GetFileName(built.Project.Archive) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllBytes(dialog.FileName, built.Archive);
        log($"Written {dialog.FileName} (place it at data/lumendata/packed/{built.Project.Archive}).");
    }

    private void log(string text)
    {
        _log.AppendText(text + Environment.NewLine);
    }

    private void updateStatus()
    {
        if (_player is null && !_busy)
            _status.Text = _project is null ? "  File › New or Open a Lumen movie (*.lumen.json)." : $"  {(_dirty ? "unsaved · " : "")}F5 builds and previews · game: {gameRoot ?? "not set (File › Game folder)"}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
