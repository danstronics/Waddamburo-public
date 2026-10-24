using System.Globalization;
using System.Text.RegularExpressions;
using Waddamburo.Formats.Lmb;

namespace LumenStudio;

/// <summary>A dynamic text field's initial HTML (font, size, colour, alignment and default text).</summary>
internal sealed class TextFieldPanel : UserControl
{
    private readonly Label _caption = new() { Dock = DockStyle.Top, Height = 54, Padding = new Padding(4) };
    private readonly TextBox _html = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = true, Font = Theme.MonoFont };
    private readonly TextBox _plain = new() { Dock = DockStyle.Top, Height = 26 };
    private MovieController? _movie;
    private LmbTextDefinition? _field;

    public TextFieldPanel()
    {
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
        buttons.Controls.Add(Ui.Button("Apply HTML", apply, "Store the edited HTML for this field (a new pool string; other fields are unaffected)"));
        buttons.Controls.Add(Ui.Button("Set text only", applyText, "Replace just the visible text inside the HTML"));
        Controls.Add(_html);
        Controls.Add(Ui.Caption("Initial HTML"));
        Controls.Add(_plain);
        Controls.Add(Ui.Caption("Visible default text"));
        Controls.Add(_caption);
        Controls.Add(buttons);
    }

    public void Show(MovieController? movie, Selection? selection)
    {
        _movie = movie;
        _field = selection is null || movie is null ? null : movie.Document.Definition.Texts.FirstOrDefault(t => t.CharacterId == selection.Node.CharacterId);
        Enabled = _field is not null;
        if (_field is null)
        {
            _caption.Text = "Select a text field on the stage (orange outlines) to edit its HTML.\r\nMost visible game text is baked into textures: use the Texture tab.";
            _html.Text = ""; _plain.Text = "";
            return;
        }
        var html = movie!.StringAt(_field.HtmlStringIndex) ?? "";
        var name = selection!.Node.Name;
        var scripts = name.Length == 0 ? 0 : movie.Document.Definition.Actions.Count(a => Avm1Assembly.StringReferences(a.Code)
            .Any(i => movie.StringAt((uint)i) == name));
        _caption.Text = $"Text field {_field.CharacterId}{(name.Length > 0 ? $" '{name}'" : "")} · font size {_field.FontSize.ToString("0.#", CultureInfo.InvariantCulture)}"
            + (scripts > 0 ? $"\r\nSet at runtime by {scripts} script(s); the game may overwrite the default text." : "\r\nShows its default text unless a script or the game sets it.");
        _html.Text = html;
        _plain.Text = visibleText(html);
    }

    private static string visibleText(string html) => System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

    private void apply()
    {
        if (_movie is null || _field is null) return;
        var id = _field.CharacterId; var html = _html.Text;
        _movie.Edit(d => d.SetTextHtml(id, html));
    }

    private void applyText()
    {
        if (_movie is null || _field is null) return;
        var html = _html.Text;
        var encoded = System.Net.WebUtility.HtmlEncode(_plain.Text);
        // Replace the innermost text run, keeping every tag (alignment, font, colour).
        var match = Regex.Match(html, ">([^<>]*)</");
        _html.Text = match.Success ? html[..match.Groups[1].Index] + encoded + html[(match.Groups[1].Index + match.Groups[1].Length)..] : encoded;
        apply();
    }
}

/// <summary>The movie's string pool: search, usages and in-place editing (for script-driven text).</summary>
internal sealed class StringsPanel : UserControl
{
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter strings…" };
    private readonly CheckBox _japanese = new() { Text = "Only Japanese text", AutoSize = true, Dock = DockStyle.Top };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Label _usages = new() { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(4) };
    private MovieController? _movie;

    public StringsPanel()
    {
        _list.Columns.Add("#", 52); _list.Columns.Add("Value", 400);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
        buttons.Controls.Add(Ui.Button("Edit…", edit, "Change this string everywhere it is used (Enter)"));
        Controls.Add(_list); Controls.Add(_japanese); Controls.Add(_filter); Controls.Add(_usages); Controls.Add(buttons);
        _filter.TextChanged += (_, _) => reload();
        _japanese.CheckedChanged += (_, _) => reload();
        _list.DoubleClick += (_, _) => Ui.Guard(this, edit);
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Ui.Guard(this, edit); };
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_movie is null || selectedIndex() is not int index) { _usages.Text = ""; return; }
            var uses = _movie.Document.StringUsages(index);
            _usages.Text = uses.Count == 0 ? "Not referenced." : "Used by: " + string.Join("; ", uses.Take(8)) + (uses.Count > 8 ? $"; +{uses.Count - 8} more" : "");
        };
    }

    public void SetMovie(MovieController? movie) { _movie = movie; reload(); }

    public void Reload() => reload();

    private int? selectedIndex() => _list.SelectedItems.Count == 0 ? null : (int)_list.SelectedItems[0].Tag!;

    private static bool isJapanese(string s) => s.Any(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '＀' and <= '￯');

    private void reload()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            if (_movie is null) return;
            var filter = _filter.Text;
            foreach (var s in _movie.Document.Definition.Strings)
            {
                if (filter.Length > 0 && !s.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (_japanese.Checked && !isJapanese(s.Value)) continue;
                _list.Items.Add(new ListViewItem([s.Index.ToString(CultureInfo.InvariantCulture), s.Value.Replace("\n", "⏎", StringComparison.Ordinal)]) { Tag = s.Index });
                if (_list.Items.Count >= 5000) break;
            }
        }
        finally { _list.EndUpdate(); }
    }

    private void edit()
    {
        if (_movie is null || selectedIndex() is not int index) return;
        var current = _movie.Document.Definition.Strings[index].Value;
        var uses = _movie.Document.StringUsages(index);
        var risky = uses.Any(u => u.StartsWith("label", StringComparison.Ordinal) || u.StartsWith("instance name", StringComparison.Ordinal) || u.StartsWith("export name", StringComparison.Ordinal));
        var warning = risky ? "This string is an identifier (label / instance / export name): scripts and the game look it up by name, so renaming it can break the scene.\r\n" : "";
        var value = Ui.Prompt(FindForm(), $"String {index}", warning + $"Used by {uses.Count} reference(s).", current, multiline: true);
        if (value is null || value == current) return;
        _movie.Edit(d => d.SetString(index, value.Replace("\r\n", "\n", StringComparison.Ordinal)));
        reload();
    }
}

/// <summary>Frame scripts of the viewed timeline; opens the AVM1 editor.</summary>
internal sealed class ScriptsPanel : UserControl
{
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _preview = new() { Dock = DockStyle.Bottom, Height = 220, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.MonoFont };
    private MovieController? _movie;

    public ScriptsPanel()
    {
        _list.Columns.Add("Frame", 60); _list.Columns.Add("Action", 60); _list.Columns.Add("Summary", 300);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        buttons.Controls.Add(Ui.Button("Script editor…", () => OpenEditor?.Invoke(selectedAction()), "Edit Lumen AVM1 assembly (Ctrl+Shift+S)"));
        buttons.Controls.Add(Ui.Button("Go to frame", () => { if (_list.SelectedItems.Count > 0) GoToFrame?.Invoke((int)((ValueTuple<int, uint>)_list.SelectedItems[0].Tag!).Item1); }));
        Controls.Add(_list); Controls.Add(_preview); Controls.Add(buttons);
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_movie is null || selectedAction() is not int action || action >= _movie.Document.Definition.Actions.Length) { _preview.Text = ""; return; }
            _preview.Text = ScriptSummary.Pseudocode(_movie.Document.Definition.Actions[action], _movie.Document.Definition.Strings);
        };
        _list.DoubleClick += (_, _) => OpenEditor?.Invoke(selectedAction());
    }

    public event Action<int?>? OpenEditor;
    public event Action<int>? GoToFrame;

    private int? selectedAction() => _list.SelectedItems.Count == 0 ? null : (int)((ValueTuple<int, uint>)_list.SelectedItems[0].Tag!).Item2;

    public void SetMovie(MovieController? movie) { _movie = movie; Reload(); }

    public void Reload()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            if (_movie is null) return;
            var model = TimelineModel.Build(_movie.Document.Definition, _movie.ViewSprite);
            foreach (var (frame, action) in model.Actions)
            {
                var summary = action < _movie.Document.Definition.Actions.Length
                    ? ScriptSummary.OneLine(_movie.Document.Definition.Actions[(int)action], _movie.Document.Definition.Strings) : "(missing)";
                _list.Items.Add(new ListViewItem([frame.ToString(CultureInfo.InvariantCulture), action.ToString(CultureInfo.InvariantCulture), summary]) { Tag = (frame, action) });
            }
        }
        finally { _list.EndUpdate(); }
    }
}
