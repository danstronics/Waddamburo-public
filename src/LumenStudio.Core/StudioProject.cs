using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumenStudio.Core;

/// <summary>
/// A LumenStudio project (<c>*.lstudio</c>): the unmodified game data, the overlay folder that receives
/// only modified archives, optional linked layers (e.g. a separately modded copy of a scene) and the
/// clone-propagation settings. Cache, journal and saved images live in <c>&lt;project&gt;.data</c>.
/// </summary>
public sealed class StudioProject
{
    public const string Extension = ".lstudio";
    public const string MainLayer = "main";

    public int Version { get; set; } = 1;

    /// <summary>The unmodified <c>data/lumendata/packed</c> folder: clone identity and change detection.</summary>
    public string BasePacked { get; set; } = "";

    /// <summary>The overlay <c>packed</c> folder; only archives that differ from the base are written here.</summary>
    public string OutputPacked { get; set; } = "";

    /// <summary>The USRDIR used to build Waddamburo previews (don3d, sound, font). Defaults to the base's USRDIR.</summary>
    public string? GameRoot { get; set; }

    public string? WaddamburoExecutable { get; set; }

    public string? FontPath { get; set; }

    public List<LinkedLayer> Linked { get; set; } = [];

    /// <summary>Textures excluded from clone propagation, as <see cref="TextureId.Key"/>.</summary>
    public SortedSet<string> Unlinked { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] public string Path { get; private set; } = "";

    [JsonIgnore] public string DataDirectory => Path + ".data";

    [JsonIgnore]
    public IReadOnlyList<Layer> Layers =>
        [new Layer(MainLayer, BasePacked, OutputPacked, AllowResize: true, ProtectMovies: false),
         .. Linked.Select(l => new Layer(l.Name, l.SourcePacked, l.OutputPacked, l.AllowResize, l.ProtectMovies))];

    public Layer Layer(string name) => Layers.FirstOrDefault(l => l.Name == name)
        ?? throw new KeyNotFoundException($"Unknown layer '{name}'.");

    public static StudioProject Create(string path, string basePacked, string outputPacked)
    {
        var project = new StudioProject
        {
            BasePacked = PackedFolder.Resolve(basePacked) ?? throw new DirectoryNotFoundException($"No data/lumendata/packed folder in {basePacked}."),
            OutputPacked = System.IO.Path.GetFullPath(outputPacked),
            Path = System.IO.Path.GetFullPath(path),
        };
        project.Validate();
        project.Save();
        return project;
    }

    public static StudioProject Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var project = JsonSerializer.Deserialize(File.ReadAllBytes(full), StudioJson.Default.StudioProject)
            ?? throw new InvalidDataException("Invalid LumenStudio project.");
        project.Path = full;
        project.Validate();
        return project;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        AtomicFile.Write(Path, JsonSerializer.SerializeToUtf8Bytes(this, StudioJson.Default.StudioProject));
    }

    public void Validate()
    {
        if (!Directory.Exists(BasePacked)) throw new DirectoryNotFoundException($"The original game data folder is missing: {BasePacked}");
        var names = new HashSet<string>(StringComparer.Ordinal) { MainLayer };
        foreach (var layer in Layers)
        {
            if (layer.Name != MainLayer && !names.Add(layer.Name)) throw new InvalidDataException($"Duplicate layer name '{layer.Name}'.");
            if (PathsOverlap(layer.SourcePacked, layer.OutputPacked))
                throw new InvalidDataException($"Layer '{layer.Name}': the output folder must be separate from its source.");
        }
        foreach (var output in Layers.Select(l => l.OutputPacked))
            foreach (var source in Layers.Select(l => l.SourcePacked))
                if (PathsOverlap(source, output))
                    throw new InvalidDataException($"An output folder overlaps source data: {output}. Sources are never written.");
    }

    public static bool PathsOverlap(string a, string b)
    {
        static string norm(string p) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p)) + System.IO.Path.DirectorySeparatorChar;
        var x = norm(a); var y = norm(b);
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class LinkedLayer
{
    public string Name { get; set; } = "";
    public string SourcePacked { get; set; } = "";
    public string OutputPacked { get; set; } = "";
    /// <summary>When false, propagated textures are scaled to this layer's original dimensions.</summary>
    public bool AllowResize { get; set; } = true;
    /// <summary>When true (the default), only texture pixels are ever changed in this layer.</summary>
    public bool ProtectMovies { get; set; } = true;
}

/// <summary>A source of archives and the overlay folder its modified archives are written to.</summary>
public sealed record Layer(string Name, string SourcePacked, string OutputPacked, bool AllowResize, bool ProtectMovies);

/// <summary>An archive inside a layer, by path relative to its <c>packed</c> folder (forward slashes).</summary>
public readonly record struct ArchiveId(string Layer, string Path)
{
    public override string ToString() => Layer == StudioProject.MainLayer ? Path : $"[{Layer}] {Path}";
}

/// <summary>A texture by archive and directory index.</summary>
public readonly record struct TextureId(ArchiveId Archive, int Index)
{
    public string Key => $"{Archive.Layer}|{Archive.Path}|{Index}";

    public static TextureId Parse(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 3) throw new FormatException($"Invalid texture key '{key}'.");
        return new TextureId(new ArchiveId(parts[0], parts[1]), int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
    }

    public override string ToString() => $"{Archive} #{Index}";
}

public static class PackedFolder
{
    /// <summary>Accepts a USRDIR, data, lumendata or packed folder and returns its packed folder.</summary>
    public static string? Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var candidate in new[] { full, Path.Combine(full, "packed"), Path.Combine(full, "lumendata", "packed"), Path.Combine(full, "data", "lumendata", "packed") })
            if (Directory.Exists(candidate) && Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate)).Equals("packed", StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }

    /// <summary>The USRDIR that contains a packed folder (<c>USRDIR/data/lumendata/packed</c>), when it has that shape.</summary>
    public static string? GameRootOf(string packed)
    {
        var lumendata = Directory.GetParent(Path.TrimEndingDirectorySeparator(packed));
        var data = lumendata?.Parent;
        return data?.Parent is { } root && data.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
            && lumendata!.Name.Equals("lumendata", StringComparison.OrdinalIgnoreCase) ? root.FullName : null;
    }

    public static IEnumerable<string> Archives(string packed) =>
        Directory.Exists(packed)
            ? Directory.EnumerateFiles(packed, "*.ddp", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(packed, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
            : [];
}

public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StudioProject))]
[JsonSerializable(typeof(TextureIndexCache))]
[JsonSerializable(typeof(Journal))]
internal sealed partial class StudioJson : JsonSerializerContext;
