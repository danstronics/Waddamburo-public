using System.Text.Json;
using Waddamburo.Formats.Nut;
using Waddamburo.Upscale;

namespace LumenStudio.Core;

/// <summary>An R,G,B,A image.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Rgba)
{
    public RgbaImage Resize(int width, int height) =>
        width == Width && height == Height ? this : new RgbaImage(width, height, ImageOps.Resize(Rgba, Width, Height, width, height));

    public string Hash => TextureIndex.PixelHash(Width, Height, Rgba);

    public static RgbaImage FromPng(byte[] png)
    {
        var (width, height, rgba) = PngFile.Decode(png);
        return new RgbaImage(checked((int)width), checked((int)height), rgba);
    }

    public byte[] ToPng() => PngFile.Encode((uint)Width, (uint)Height, Rgba);

    public static RgbaImage Decode(NutTexture texture) => new(texture.Width, texture.Height, NutTextureDecoder.DecodeRgba8(texture));
}

/// <summary>
/// A pending texture replacement. <see cref="EncodedNut"/> carries an exact NUT when it was copied from
/// a clone with the same format and size, so propagation is bit-exact instead of re-encoded.
/// </summary>
public sealed record PendingTexture(RgbaImage Image, DateTime TimeUtc, TextureId Origin, byte[]? EncodedNut = null);

/// <summary>Unsaved edits: replaced textures and edited LMB movies, by archive.</summary>
public sealed class ChangeSet
{
    public Dictionary<TextureId, PendingTexture> Textures { get; } = [];
    public Dictionary<(ArchiveId Archive, string Movie), byte[]> Movies { get; } = [];

    public bool IsEmpty => Textures.Count == 0 && Movies.Count == 0;

    public IEnumerable<ArchiveId> Archives => Textures.Keys.Select(t => t.Archive).Concat(Movies.Keys.Select(m => m.Archive)).Distinct();

    public ChangeSet Clone()
    {
        var copy = new ChangeSet();
        foreach (var pair in Textures) copy.Textures[pair.Key] = pair.Value;
        foreach (var pair in Movies) copy.Movies[pair.Key] = pair.Value;
        return copy;
    }

    public void Remove(ArchiveId archive)
    {
        foreach (var key in Textures.Keys.Where(k => k.Archive == archive).ToArray()) Textures.Remove(key);
        foreach (var key in Movies.Keys.Where(k => k.Archive == archive).ToArray()) Movies.Remove(key);
    }
}

/// <summary>Saved texture edits with exact times, used by "most recent edit wins" propagation.</summary>
public sealed class Journal
{
    public List<JournalEntry> Entries { get; set; } = [];

    public static Journal Load(StudioProject project)
    {
        var path = FilePath(project);
        if (!File.Exists(path)) return new Journal();
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(path), StudioJson.Default.Journal) ?? new Journal(); }
        catch (JsonException) { return new Journal(); }
    }

    public void Save(StudioProject project) =>
        AtomicFile.Write(FilePath(project), JsonSerializer.SerializeToUtf8Bytes(this, StudioJson.Default.Journal));

    /// <summary>When the texture's current content was produced, if LumenStudio recorded it.</summary>
    public DateTime? TimeOf(TextureId id, string contentHash) =>
        Entries.Where(e => e.Texture == id.Key && e.Hash == contentHash).Select(e => (DateTime?)e.TimeUtc).Max();

    public void Record(StudioProject project, TextureId id, RgbaImage image, DateTime timeUtc, TextureId origin)
    {
        var hash = image.Hash;
        var images = Path.Combine(project.DataDirectory, "images");
        var file = Path.Combine(images, hash + ".png");
        if (!File.Exists(file)) AtomicFile.Write(file, image.ToPng());
        Entries.Add(new JournalEntry { TimeUtc = timeUtc, Texture = id.Key, Origin = origin.Key, Hash = hash, Width = image.Width, Height = image.Height });
    }

    public static string ImagePath(StudioProject project, string hash) => Path.Combine(project.DataDirectory, "images", hash + ".png");

    private static string FilePath(StudioProject project) => Path.Combine(project.DataDirectory, "journal.json");
}

public sealed class JournalEntry
{
    public DateTime TimeUtc { get; set; }
    public string Texture { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}
