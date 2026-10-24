namespace LumenStudio;

/// <summary>The dark editor palette and recursive control styling.</summary>
internal static class Theme
{
    internal static readonly Color Background = Color.FromArgb(27, 29, 35);
    internal static readonly Color Surface = Color.FromArgb(36, 39, 47);
    internal static readonly Color SurfaceRaised = Color.FromArgb(46, 50, 60);
    internal static readonly Color Border = Color.FromArgb(62, 67, 80);
    internal static readonly Color Foreground = Color.FromArgb(224, 228, 237);
    internal static readonly Color Muted = Color.FromArgb(150, 158, 175);
    internal static readonly Color Accent = Color.FromArgb(55, 85, 109);
    internal static readonly Color Highlight = Color.FromArgb(89, 220, 244);
    internal static readonly Color Modified = Color.FromArgb(240, 190, 90);
    internal static readonly Color Pending = Color.FromArgb(120, 210, 140);
    internal static readonly Color Danger = Color.FromArgb(235, 100, 100);

    public static Font UiFont { get; } = new("Segoe UI", 9);
    public static Font MonoFont { get; } = new("Consolas", 9.5f);
    public static Font BoldFont { get; } = new("Segoe UI", 9, FontStyle.Bold);

    /// <summary>Call first in a form's constructor: its pixel sizes are authored at 96 DPI and scaled on display.</summary>
    public static void BeginScaling(Form form)
    {
        form.SuspendLayout();
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        s_scaling.Add(form);
    }

    private static readonly HashSet<Form> s_scaling = [];

    /// <summary>Scales a 96-DPI length for a control's current DPI.</summary>
    public static int Scale(Control control, int pixels) => (int)Math.Round(pixels * control.DeviceDpi / 96f);

    /// <summary>Themes a control tree; for a form begun with <see cref="BeginScaling"/>, also applies DPI scaling.</summary>
    public static void Apply(Control control)
    {
        applyChildren(control);
        if (control is Form form && s_scaling.Remove(form)) form.ResumeLayout(true);
    }

    private static void applyChildren(Control control)
    {
        control.BackColor = control is TextBoxBase or ListBox or ComboBox or PropertyGrid or ListView or TreeView ? Surface : Background;
        control.ForeColor = Foreground;
        if (control is Form form) form.Font = UiFont;
        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat; button.BackColor = SurfaceRaised;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Accent;
        }
        if (control is CheckBox check) check.FlatStyle = FlatStyle.Flat;
        if (control is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
        if (control is TextBoxBase text) text.BorderStyle = BorderStyle.FixedSingle;
        if (control is ListView list) list.BorderStyle = BorderStyle.None;
        if (control is TreeView tree) { tree.BorderStyle = BorderStyle.None; tree.LineColor = Border; }
        if (control is SplitContainer split) split.BackColor = Border;
        if (control is TabControl tabs) { tabs.DrawMode = TabDrawMode.OwnerDrawFixed; tabs.DrawItem -= drawTab; tabs.DrawItem += drawTab; }
        if (control is TabPage page) { page.BackColor = Background; page.UseVisualStyleBackColor = false; }
        if (control is PropertyGrid grid)
        {
            grid.ViewBackColor = Surface; grid.ViewForeColor = Foreground;
            grid.HelpBackColor = Background; grid.HelpForeColor = Foreground;
            grid.CategoryForeColor = Foreground; grid.CategorySplitterColor = Background;
            grid.LineColor = Background; grid.SelectedItemWithFocusBackColor = Accent;
            grid.SelectedItemWithFocusForeColor = Foreground; grid.ViewBorderColor = Surface;
            grid.DisabledItemForeColor = Muted;
        }
        if (control is ToolStrip strip)
        {
            strip.Renderer = new ToolStripProfessionalRenderer(new Palette()) { RoundedEdges = false };
            foreach (ToolStripItem item in strip.Items) themeItem(item);
        }
        if (control.ContextMenuStrip is { } menu) Apply(menu);
        foreach (Control child in control.Controls) applyChildren(child);
    }

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new Palette()) { RoundedEdges = false };
        menu.BackColor = Surface; menu.ForeColor = Foreground;
        foreach (ToolStripItem item in menu.Items) themeItem(item);
    }

    private static void drawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs) return;
        var selected = e.Index == tabs.SelectedIndex;
        using var back = new SolidBrush(selected ? SurfaceRaised : Background);
        e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Highlight);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Bottom - 2, e.Bounds.Width, 2);
        }
        TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, UiFont, e.Bounds, selected ? Foreground : Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void themeItem(ToolStripItem item)
    {
        item.ForeColor = Foreground;
        if (item is ToolStripDropDownItem drop)
        {
            drop.DropDown.BackColor = Surface;
            foreach (ToolStripItem child in drop.DropDownItems) themeItem(child);
        }
    }

    private sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Background;
        public override Color ToolStripGradientMiddle => Background;
        public override Color ToolStripGradientEnd => Background;
        public override Color ToolStripBorder => Background;
        public override Color MenuStripGradientBegin => Background;
        public override Color MenuStripGradientEnd => Background;
        public override Color StatusStripGradientBegin => Background;
        public override Color StatusStripGradientEnd => Background;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
        public override Color MenuItemSelected => Accent;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelectedGradientBegin => Accent;
        public override Color MenuItemSelectedGradientEnd => Accent;
        public override Color MenuItemPressedGradientBegin => Surface;
        public override Color MenuItemPressedGradientMiddle => Surface;
        public override Color MenuItemPressedGradientEnd => Surface;
        public override Color ButtonSelectedGradientBegin => Accent;
        public override Color ButtonSelectedGradientMiddle => Accent;
        public override Color ButtonSelectedGradientEnd => Accent;
        public override Color ButtonSelectedBorder => Accent;
        public override Color ButtonCheckedGradientBegin => Accent;
        public override Color ButtonCheckedGradientMiddle => Accent;
        public override Color ButtonCheckedGradientEnd => Accent;
        public override Color ButtonPressedGradientBegin => Accent;
        public override Color ButtonPressedGradientMiddle => Accent;
        public override Color ButtonPressedGradientEnd => Accent;
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
    }
}

/// <summary>Small helpers shared by the windows and dialogs.</summary>
internal static class Ui
{
    public static Label Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(), Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted,
        Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(2, 0, 0, 2),
    };

    public static Button Button(string text, Action action, string? tip = null)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(2), Padding = new Padding(6, 0, 6, 0) };
        button.Click += (_, _) => Guard(button, action);
        if (tip is not null) new ToolTip().SetToolTip(button, tip);
        return button;
    }

    /// <summary>Automated runs (--capture): errors go to this file instead of a blocking message box.</summary>
    public static string? HeadlessErrorLog { get; set; }

    public static void Guard(Control? owner, Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (HeadlessErrorLog is not null) { File.AppendAllText(HeadlessErrorLog, exception + Environment.NewLine); return; }
            MessageBox.Show(owner?.FindForm(), exception.Message, "LumenStudio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static string? Prompt(IWin32Window? owner, string title, string description, string value, bool multiline = false)
    {
        using var dialog = new Form
        {
            Text = title, Width = 560, Height = multiline ? 380 : 190, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = multiline ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
        };
        Theme.BeginScaling(dialog);
        var input = new TextBox { Dock = multiline ? DockStyle.Fill : DockStyle.Top, Text = value, Multiline = multiline, ScrollBars = multiline ? ScrollBars.Both : ScrollBars.None, AcceptsReturn = multiline, WordWrap = false };
        var caption = new Label { Text = description, Dock = DockStyle.Top, Height = 48, Padding = new Padding(6) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        buttons.Controls.AddRange([cancel, ok]);
        dialog.Controls.Add(input); dialog.Controls.Add(caption); dialog.Controls.Add(buttons);
        dialog.AcceptButton = multiline ? null : ok; dialog.CancelButton = cancel;
        Theme.Apply(dialog);
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }

    public static bool Confirm(IWin32Window? owner, string text, string title = "LumenStudio") =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
}
