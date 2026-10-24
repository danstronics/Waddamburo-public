using System.Text;
using Waddamburo.Formats.Ddp;

namespace LumenStudio.Core;

/// <summary>Reads archives through the overlay: a layer's output copy when present, otherwise its source.</summary>
public sealed class ArchiveStore(StudioProject project)
{
    public StudioProject Project { get; } = project;

    public IEnumerable<ArchiveId> Archives() =>
        Project.Layers.SelectMany(layer => PackedFolder.Archives(layer.SourcePacked).Select(path => new ArchiveId(layer.Name, path)));

    public string SourcePath(ArchiveId id) => Path.Combine(Project.Layer(id.Layer).SourcePacked, id.Path);

    public string OutputPath(ArchiveId id) => Path.Combine(Project.Layer(id.Layer).OutputPacked, id.Path);

    public bool IsModified(ArchiveId id) => File.Exists(OutputPath(id));

    public string CurrentPath(ArchiveId id) => IsModified(id) ? OutputPath(id) : SourcePath(id);

    public byte[] ReadSource(ArchiveId id) => File.ReadAllBytes(SourcePath(id));

    public byte[] ReadCurrent(ArchiveId id) => File.ReadAllBytes(CurrentPath(id));

    public DateTime CurrentTimestampUtc(ArchiveId id) => File.GetLastWriteTimeUtc(CurrentPath(id));

    /// <summary>Texture names from the source folder's packlist.txt, by directory index; null when absent.</summary>
    public PackList? PackList(ArchiveId id)
    {
        var file = Path.Combine(Path.GetDirectoryName(SourcePath(id))!, "packlist.txt");
        if (!File.Exists(file) && id.Layer != StudioProject.MainLayer)
            // Linked copies often lack packlists; the original archive at the same path names the same textures.
            file = Path.Combine(Path.GetDirectoryName(Path.Combine(Project.BasePacked, id.Path))!, "packlist.txt");
        return File.Exists(file) ? Core.PackList.Parse(File.ReadAllBytes(file)) : null;
    }

    /// <summary>
    /// Writes an archive to the layer's output. An archive identical to its source is removed from the
    /// output instead, so the overlay only ever holds modified files.
    /// </summary>
    public void WriteOutput(ArchiveId id, byte[] bytes)
    {
        _ = DdpArchive.Open(bytes);
        var output = OutputPath(id);
        if (bytes.AsSpan().SequenceEqual(ReadSource(id)))
        {
            if (File.Exists(output)) File.Delete(output);
            return;
        }
        AtomicFile.Write(output, bytes);
    }

    public void RevertOutput(ArchiveId id)
    {
        var output = OutputPath(id);
        if (File.Exists(output)) File.Delete(output);
    }
}

/// <summary>packlist.txt: texture count, then each movie name followed by its tab-indented NUT names.</summary>
public sealed record PackList(int TextureCount, IReadOnlyList<(string Movie, IReadOnlyList<string> Textures)> Movies)
{
    public static PackList Parse(byte[] bytes)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var count = int.TryParse(lines.FirstOrDefault()?.Trim(), out var value) ? value : 0;
        var movies = new List<(string, IReadOnlyList<string>)>();
        List<string>? current = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t') { current?.Add(line.Trim().Replace('\\', '/')); continue; }
            current = [];
            movies.Add((line.Trim(), current));
        }
        return new PackList(count, movies);
    }

    /// <summary>Name of a texture by movie and movie-local index.</summary>
    public string? TextureName(string movie, int localIndex)
    {
        foreach (var (name, textures) in Movies)
            if (name == movie) return localIndex < textures.Count ? textures[localIndex] : null;
        return null;
    }
}
