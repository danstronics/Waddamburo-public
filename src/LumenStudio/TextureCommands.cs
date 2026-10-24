using System.Globalization;
using LumenStudio.Core;

namespace LumenStudio;

/// <summary>Texture actions shared by the movie window and the texture-archive window.</summary>
internal static class TextureCommands
{
    public static string Name(StudioSession studio, TextureId id) => studio.Index?.Get(id)?.Name ?? $"{id.Archive.Path} #{id.Index}";

    public static bool Edit(IWin32Window owner, StudioSession studio, TextureId id, Action<string> log)
    {
        var image = studio.TextureImage(id);
        var targets = studio.PropagationTargets(id, Propagation.Clones);
        var allClones = studio.Index is null ? 0 : studio.Index.Family(id).Count - 1;
        var note = allClones > targets.Count ? $" ({allClones - targets.Count} unlinked or blank)" : "";
        using var editor = new TextureEditorForm(image, Name(studio, id), targets.Count, note);
        if (editor.ShowDialog(owner) != DialogResult.OK) return false;
        Apply(studio, id, editor.Result, editor.Propagate, log);
        return true;
    }

    public static void Apply(StudioSession studio, TextureId id, RgbaImage image, bool propagate, Action<string> log)
    {
        var changed = studio.ReplaceTexture(id, image, propagate ? Propagation.Clones : Propagation.None);
        var original = studio.Index?.Get(id);
        log($"Texture {Name(studio, id)} → {image.Width}×{image.Height}" +
            (original is not null && (original.Width != image.Width || original.Height != image.Height) ? $" (was {original.Width}×{original.Height})" : "") +
            (changed.Count > 1 ? $", also {changed.Count - 1} clone(s) in {changed.Skip(1).Select(c => c.Archive).Distinct().Count()} archive(s)" : "") + ". Save all to write it.");
    }

    public static bool Replace(IWin32Window owner, StudioSession studio, TextureId id, Action<string> log)
    {
        using var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tga;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        var image = Imaging.Load(dialog.FileName);
        var current = studio.TextureImage(id);
        var propagate = true;
        var targets = studio.PropagationTargets(id, Propagation.Clones).Count;
        if (targets > 0)
        {
            var answer = MessageBox.Show(owner, $"Also replace {targets} linked clone(s) of this texture?", "Replace texture", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            propagate = answer == DialogResult.Yes;
        }
        if (image.Width != current.Width || image.Height != current.Height)
            log($"Note: the image is {image.Width}×{image.Height}, the texture {current.Width}×{current.Height}. The texture is resized; it keeps its on-screen size.");
        Apply(studio, id, image, propagate, log);
        return true;
    }

    public static void Export(IWin32Window owner, StudioSession studio, TextureId id)
    {
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = safeName(Name(studio, id)) + ".png" };
        if (dialog.ShowDialog(owner) == DialogResult.OK) Imaging.SavePng(studio.TextureImage(id), dialog.FileName);
    }

    public static void Revert(IWin32Window owner, StudioSession studio, TextureId id, Action<string> log)
    {
        var targets = studio.PropagationTargets(id, Propagation.Clones).Count;
        var propagate = targets > 0 && Ui.Confirm(owner, $"Also revert {targets} linked clone(s)?");
        studio.RevertTexture(id, propagate ? Propagation.Clones : Propagation.None);
        log($"Reverted {Name(studio, id)} to the original{(propagate ? $" with {targets} clone(s)" : "")}. Save all to write it.");
    }

    public static void ShowClones(IWin32Window owner, StudioSession studio, TextureId id)
    {
        using var dialog = new CloneBrowserForm(studio, id);
        dialog.ShowDialog(owner);
    }

    /// <summary>Exports textures as NNNN_name.png (NNNN = position in <paramref name="ids"/>).</summary>
    public static void ExportAll(IWin32Window owner, StudioSession studio, IReadOnlyList<TextureId> ids, Action<string> log)
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder for the textures (PNG, numbered and named)", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        for (var i = 0; i < ids.Count; i++)
            Imaging.SavePng(studio.TextureImage(ids[i]), Path.Combine(dialog.SelectedPath, $"{i:D4}_{safeName(Name(studio, ids[i]))}.png"));
        log($"Exported {ids.Count} textures to {dialog.SelectedPath}. Edit them, then use Import edited PNG folder.");
    }

    public static void ImportFolder(IWin32Window owner, StudioSession studio, IReadOnlyList<TextureId> ids, Action<string> log)
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder exported by 'Export all textures' (files named NNNN_name.png)", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        var propagate = Ui.Confirm(owner, "Propagate each changed texture to its linked clones?");
        var changed = 0;
        foreach (var file in Directory.GetFiles(dialog.SelectedPath, "*.png").Order(StringComparer.Ordinal))
        {
            var prefix = Path.GetFileName(file).Split('_')[0];
            if (!int.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) || position < 0 || position >= ids.Count) continue;
            var image = Imaging.Load(file);
            if (image.Hash == studio.TextureImage(ids[position]).Hash) continue;
            studio.ReplaceTexture(ids[position], image, propagate ? Propagation.Clones : Propagation.None);
            changed++;
        }
        log($"Imported {changed} changed texture(s) from {dialog.SelectedPath}. Save all to write them.");
    }

    private static string safeName(string name) => string.Concat(Path.GetFileNameWithoutExtension(name.Replace('/', '_').Replace('#', '_'))
        .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
