using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LumenStudio.Core;

namespace LumenStudio;

/// <summary>
/// Runs Waddamburo on edited data without touching the original installation: a single movie in its
/// viewer, or the whole game on a preview USRDIR made of junctions to the original folders plus links
/// to the overlay's modified archives.
/// </summary>
internal static partial class WaddamburoLauncher
{
    public static string? FindExecutable(StudioProject project, AppSettings settings)
    {
        foreach (var candidate in new[] { project.WaddamburoExecutable, settings.WaddamburoExecutable })
            if (candidate is not null && File.Exists(candidate)) return candidate;
        // The copy published with LumenStudio is a development build, which never self-updates.
        foreach (var name in new[] { Path.Combine("waddamburo", "Waddamburo.App.exe"), Path.Combine("waddamburo", "Waddamburo.exe") })
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(bundled)) return bundled;
        }
        return null;
    }

    /// <summary>Opens one movie of a (possibly unsaved) archive in Waddamburo's viewer.</summary>
    public static void TestMovie(string executable, byte[] archive, string movie, bool layoutOnly)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LumenStudio", "preview");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "*.ddp").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6)))
            try { File.Delete(old); } catch (IOException) { }
        var path = Path.Combine(folder, $"preview-{Guid.NewGuid():N}.ddp");
        File.WriteAllBytes(path, archive);
        var arguments = $"--archive=\"{path}\" --movie=\"{movie}\"" + (layoutOnly ? " --layout-only" : "");
        start(executable, arguments);
    }

    /// <summary>Builds (or refreshes) the preview USRDIR and starts the full game on it.</summary>
    public static string TestGame(string executable, StudioProject project)
    {
        var root = BuildPreviewRoot(project);
        var arguments = $"--game-data=\"{root}\"";
        if (project.FontPath is { } font && File.Exists(font)) arguments += $" --font=\"{font}\"";
        start(executable, arguments);
        return root;
    }

    public static string BuildPreviewRoot(StudioProject project)
    {
        var gameRoot = project.GameRoot ?? PackedFolder.GameRootOf(project.BasePacked)
            ?? throw new InvalidOperationException("Set the game USRDIR in Project › Settings to preview the full game.");
        var data = Path.Combine(gameRoot, "data");
        if (!Directory.Exists(data)) throw new DirectoryNotFoundException($"No data folder in {gameRoot}.");
        var root = Path.Combine(project.DataDirectory, "preview-usrdir");
        var previewData = Path.Combine(root, "data");
        if (Directory.Exists(previewData)) deleteLinksSafely(previewData, project.DataDirectory);
        // The overlay's data folder (output packed → lumendata → data) mirrors over the original data folder.
        var outputData = Directory.GetParent(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(project.OutputPacked))!)!.FullName;
        mirror(data, previewData, outputData);
        return root;
    }

    /// <summary>Mirrors <paramref name="source"/>: untouched subtrees become junctions, overlay files are linked over originals.</summary>
    private static void mirror(string source, string target, string overlay)
    {
        if (!Directory.Exists(overlay)) { junction(target, source); return; }
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            mirror(directory, Path.Combine(target, name), Path.Combine(overlay, name));
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            var modified = Path.Combine(overlay, name);
            // Overlay files are ours: hard-link them. Original files are copied (only small ones live here).
            if (File.Exists(modified)) link(Path.Combine(target, name), modified);
            else File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
    }

    private static void junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new IOException($"Could not create a junction {link} → {target}: {process.StandardError.ReadToEnd().Trim()}");
    }

    /// <summary>A hard link to an overlay file when on the same volume (no copy, instant), otherwise a copy.</summary>
    private static void link(string link, string target)
    {
        if (CreateHardLink(link, target, 0)) return;
        File.Copy(target, link, overwrite: true);
    }

    /// <summary>
    /// Deletes a preview tree. Junctions are removed as links (never followed); hard links only drop
    /// the link. Refuses any path outside the project's data folder.
    /// </summary>
    private static void deleteLinksSafely(string path, string container)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(container) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete outside the project data folder.");
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

    private static void start(string executable, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! })?.Dispose();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Could not start {executable}: {exception.Message}", exception);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
