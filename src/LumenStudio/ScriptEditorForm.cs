using System.Globalization;
using Waddamburo.Formats.Lmb;

namespace LumenStudio;

/// <summary>Lumen AVM1 script editor: lossless assembly with a decompiled preview and the string pool.</summary>
internal sealed class ScriptEditorForm : Form
{
    private readonly MovieController _movie;
    private readonly ListBox _actions = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly RichTextBox _source = new() { Dock = DockStyle.Fill, AcceptsTab = true, WordWrap = false, DetectUrls = false, Font = new Font("Consolas", 10), HideSelection = false };
    private readonly TextBox _pseudo = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5f) };
    private readonly TextBox _strings = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 6, 8, 0) };
    private readonly uint _sprite;
    private readonly int _frame;
    private int _selected = -1;
    private string _baseline = "";
    private bool _updating;

    public ScriptEditorForm(MovieController movie, uint sprite, int frame, int? action)
    {
        Theme.BeginScaling(this);
        _movie = movie; _sprite = sprite; _frame = frame;
        Text = $"Script Editor · {movie.MovieName}"; Width = 1400; Height = 880; StartPosition = FormStartPosition.CenterParent;
        var left = new Panel { Dock = DockStyle.Left, Width = 260 };
        left.Controls.Add(_actions); left.Controls.Add(Ui.Caption("Actions (sprite@frame)"));
        var right = new SplitContainer { Dock = DockStyle.Right, Width = 420, Orientation = Orientation.Horizontal, SplitterDistance = 430 };
        right.Panel1.Controls.Add(_pseudo); right.Panel1.Controls.Add(Ui.Caption("Decompiled (read-only)"));
        right.Panel2.Controls.Add(_strings); right.Panel2.Controls.Add(Ui.Caption("String pool (F001)"));
        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(_source); center.Controls.Add(Ui.Caption("Assembly (editable)"));
        var tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        void tool(string text, Action action, string tip) { var b = new ToolStripButton(text) { ToolTipText = tip }; b.Click += (_, _) => run(action); tools.Items.Add(b); }
        tool("Validate", () => { var bytes = compile(); _pseudo.Text = ScriptSummary.Pseudocode(preview(bytes), _movie.Document.Definition.Strings); _status.Text = $"Valid: {bytes.Length} bytes; branches, blocks and string references resolve."; }, "Assemble and check without applying");
        tool("Apply (Ctrl+S)", apply, "Assemble and store in the movie (undoable from the main window)");
        tools.Items.Add(new ToolStripSeparator());
        tool("New frame script", addScript, $"Add a script at frame {frame} of the viewed timeline");
        tool("Add string…", addString, "Intern a new string; use Push string:N");
        tool("Find… (Ctrl+F)", find, "Find text in the assembly");
        var help = new Label
        {
            Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 4, 8, 0), ForeColor = Theme.Muted,
            Text = "Push accepts int:42 float:1.5 double:1.25 string:12 bool:true register:1 null undefined. Jump / If take labels (name:).\r\nFunction / With bodies use braces; lengths and branch distances are rebuilt. Raw hex operands keep their original encoding.",
        };
        Controls.Add(center); Controls.Add(right); Controls.Add(left); Controls.Add(tools); Controls.Add(help); Controls.Add(_status);
        Theme.Apply(this);
        _actions.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _actions.SelectedIndex == _selected) return;
            var next = _actions.SelectedIndex;
            if (!mayDiscard()) { _updating = true; _actions.SelectedIndex = _selected; _updating = false; return; }
            load(next);
        };
        _source.TextChanged += (_, _) => { if (!_updating) _status.Text = _source.Text == _baseline ? "No unapplied changes." : "Unapplied changes · Ctrl+S validates and applies."; };
        FormClosing += (_, e) => { if (!mayDiscard()) e.Cancel = true; };
        reload();
        if (_movie.Protected) { _source.ReadOnly = true; _status.Text = "This layer protects its movies: scripts are read-only."; }
        if (_actions.Items.Count > 0) _actions.SelectedIndex = Math.Clamp(action ?? 0, 0, _actions.Items.Count - 1);
        else _status.Text = "No scripts in this movie. Use New frame script to add one.";
    }

    private void reload()
    {
        _updating = true; _actions.BeginUpdate();
        try
        {
            _actions.Items.Clear();
            var movie = _movie.Document.Definition;
            var uses = new Dictionary<uint, List<string>>();
            foreach (var sprite in movie.Sprites)
            {
                uint frame = 0;
                foreach (var command in sprite.Timeline)
                {
                    if (command is LmbShowFrameCommand show) frame = show.Frame;
                    if (command is LmbDoActionCommand action)
                    {
                        if (!uses.TryGetValue(action.ActionIndex, out var list)) uses[action.ActionIndex] = list = [];
                        if (list.Count < 4) list.Add($"#{sprite.CharacterId}@{frame}");
                    }
                }
            }
            foreach (var action in movie.Actions)
                _actions.Items.Add($"{action.Index,4} · {action.Bytecode.Length,5} B · {string.Join(", ", uses.GetValueOrDefault((uint)action.Index) ?? ["unreferenced"])}");
            _strings.Text = string.Join("\r\n", movie.Strings.Select(s => $"{s.Index,5}: {s.Value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}"));
            _selected = -1;
        }
        finally { _actions.EndUpdate(); _updating = false; }
    }

    private void load(int index)
    {
        _selected = index; _updating = true;
        var action = index >= 0 ? _movie.Document.Definition.Actions[index] : null;
        _source.Text = _baseline = action is null ? "" : Avm1Assembly.Disassemble(action, _movie.Document.Definition.Strings);
        _pseudo.Text = action is null ? "" : ScriptSummary.Pseudocode(action, _movie.Document.Definition.Strings);
        _source.ClearUndo(); _updating = false;
        _status.Text = "No unapplied changes.";
    }

    private byte[] compile()
    {
        if (_selected < 0) throw new InvalidOperationException("Select a script first.");
        var bytes = Avm1Assembly.Assemble(_source.Text);
        var validation = new LmbEditDocument(_movie.Document.Data.Span);
        validation.SetActionBytecode(_selected, bytes);
        return bytes;
    }

    private LmbAction preview(byte[] bytes)
    {
        var validation = new LmbEditDocument(_movie.Document.Data.Span);
        validation.SetActionBytecode(_selected, bytes);
        return validation.Definition.Actions[_selected];
    }

    private void apply()
    {
        var bytes = compile(); var index = _selected;
        _movie.Edit(d => d.SetActionBytecode(index, bytes));
        reload(); _updating = true; _actions.SelectedIndex = index; _updating = false; load(index);
        _status.Text = $"Applied action {index} ({bytes.Length} bytes). Save the project to write it to the overlay.";
    }

    private void addScript()
    {
        if (!mayDiscard()) return;
        var index = -1;
        _movie.Edit(d => index = d.AddAction(_sprite, _frame, [0x07, 0x00]));
        reload(); _actions.SelectedIndex = index;
    }

    private void addString()
    {
        var text = Ui.Prompt(this, "Add string", "Text to add to the string pool:", "");
        if (text is null) return;
        uint index = 0;
        _movie.Edit(d => index = d.InternString(text));
        var selected = _selected;
        reload(); _updating = true; _actions.SelectedIndex = selected; _updating = false; _selected = selected;
        _status.Text = $"String index {index.ToString(CultureInfo.InvariantCulture)}: use Push string:{index.ToString(CultureInfo.InvariantCulture)}.";
    }

    private void find()
    {
        var text = Ui.Prompt(this, "Find", "Text to find in the assembly:", _source.SelectedText);
        if (string.IsNullOrEmpty(text)) return;
        var start = Math.Min(_source.TextLength, _source.SelectionStart + _source.SelectionLength);
        var index = _source.Find(text, start, RichTextBoxFinds.None);
        if (index < 0) index = _source.Find(text, 0, RichTextBoxFinds.None);
        if (index >= 0) { _source.Focus(); _source.ScrollToCaret(); } else _status.Text = "Not found.";
    }

    private bool mayDiscard() => _source.Text == _baseline || Ui.Confirm(this, "Discard unapplied script changes?");

    private void run(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is not OutOfMemoryException) { _status.Text = error.Message; }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { run(apply); return true; }
        if (keyData == (Keys.Control | Keys.F)) { run(find); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
