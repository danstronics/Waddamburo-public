using System.Diagnostics;

namespace LumenStudio.Authoring.Packaging;

/// <summary>
/// A throwaway USRDIR for playtesting: the original data folder mirrored by junctions (nothing copied,
/// nothing of the original written), with built archives placed over their originals.
/// </summary>
public static class PreviewRoot
{
    /// <param name="gameRoot">The original USRDIR (left untouched).</param>
    /// <param name="previewRoot">The folder to (re)create; only links and our files are ever deleted in it.</param>
    /// <param name="archives">Built archives by path relative to data/lumendata/packed.</param>
    public static string Build(string gameRoot, string previewRoot, IReadOnlyDictionary<string, byte[]> archives)
    {
        var data = Path.Combine(Path.GetFullPath(gameRoot), "data");
        if (!Directory.Exists(data)) throw new DirectoryNotFoundException($"No data folder in {gameRoot}.");
        var root = Path.GetFullPath(previewRoot);
        var previewData = Path.Combine(root, "data");
        if (Directory.Exists(previewData)) deleteLinksSafely(previewData, root);
        var overlay = archives.ToDictionary(
            pair => Path.GetFullPath(Path.Combine(previewData, "lumendata", "packed", pair.Key)),
            pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        mirror(data, previewData, overlay);
        return root;
    }

    private static void mirror(string source, string target, Dictionary<string, byte[]> overlay)
    {
        var prefix = target + Path.DirectorySeparatorChar;
        if (!overlay.Keys.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            junction(target, source);
            return;
        }
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source))
            mirror(directory, Path.Combine(target, Path.GetFileName(directory)), overlay);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (overlay.TryGetValue(destination, out var bytes)) File.WriteAllBytes(destination, bytes);
            else File.Copy(file, destination, overwrite: true);
        }
        foreach (var (path, bytes) in overlay)
            if (Path.GetDirectoryName(path)!.Equals(target, StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
                File.WriteAllBytes(path, bytes);
    }

    private static void junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new IOException($"Could not create a junction {link} → {target}: {process.StandardError.ReadToEnd().Trim()}");
    }

    // Junctions are removed as links (never followed into the original data).
    private static void deleteLinksSafely(string path, string container)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete outside the preview folder.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry, recursive: false);
                else deleteLinksSafely(entry, container);
            }
            else File.Delete(entry);
        }
        Directory.Delete(full, recursive: false);
    }
}
