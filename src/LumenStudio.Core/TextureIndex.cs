using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Waddamburo.Formats.Nut;

namespace LumenStudio.Core;

/// <summary>One texture: its identity, names, original (source) and current pixel hashes.</summary>
public sealed record TextureEntry(
    TextureId Id,
    string Movie,
    int LocalIndex,
    string? Name,
    int Width,
    int Height,
    NutPixelFormat Format,
    string SourceHash,
    string CurrentHash,
    int CurrentWidth,
    int CurrentHeight,
    NutPixelFormat CurrentFormat,
    bool Uniform,
    int ChangedPixels)
{
    public bool Modified => SourceHash != CurrentHash;

    /// <summary>A real edit (resized, or at least a few visibly different texels), not re-encoding noise.</summary>
    public bool SignificantlyModified => Modified && (ChangedPixels < 0 || ChangedPixels >= SignificantPixels);

    public const int SignificantPixels = 4;
}

/// <summary>
/// Pixel-content index of every texture in every layer. Textures whose original pixels are identical
/// (different names, archives, formats) form a clone family. A modified texture keeps the family of
/// its original, and a linked layer's texture joins the family whose original or current content it
/// matches, so copies made from already translated archives are still recognised.
/// </summary>
public sealed class TextureIndex
{
    private const int CacheVersion = 5;
    private readonly ArchiveStore _store;
    private readonly string? _cachePath;
    private readonly ConcurrentDictionary<ArchiveId, ArchiveScan> _scans = new();
    private Dictionary<string, List<TextureEntry>> _families = [];
    private Dictionary<TextureId, TextureEntry> _entries = [];
    private Dictionary<string, string> _currentToFamily = [];
    private HashSet<string> _originals = new(StringComparer.Ordinal);
    private HashSet<string> _significantCurrent = new(StringComparer.Ordinal);

    private TextureIndex(ArchiveStore store, string? cachePath)
    {
        _store = store;
        _cachePath = cachePath;
    }

    public IReadOnlyCollection<TextureEntry> Entries => _entries.Values;

    public static TextureIndex Build(ArchiveStore store, IProgress<IndexProgress>? progress = null, CancellationToken cancellation = default)
    {
        var cachePath = store.Project.Path.Length == 0 ? null : Path.Combine(store.Project.DataDirectory, "texture-index.json");
        var index = new TextureIndex(store, cachePath);
        var cached = index.loadCache();
        var archives = store.Archives().ToArray();
        var done = 0;
        var payloadMemo = new ConcurrentDictionary<string, (string Hash, bool Uniform)>(StringComparer.Ordinal);
        Parallel.ForEach(archives, new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, archive =>
        {
            var key = archiveKey(archive);
            var sourceStamp = stamp(store.SourcePath(archive));
            var currentStamp = stamp(store.CurrentPath(archive));
            ArchiveScan scan;
            if (cached.TryGetValue(key, out var previous) && previous.SourceStamp == sourceStamp && previous.CurrentStamp == currentStamp)
                scan = previous;
            else
            {
                try { scan = index.scan(archive, sourceStamp, currentStamp, payloadMemo); }
                catch (Exception exception) when (exception is InvalidDataException or IOException or Waddamburo.Formats.IO.FormatReadException)
                {
                    scan = new ArchiveScan { SourceStamp = sourceStamp, CurrentStamp = currentStamp, Error = exception.Message };
                }
            }
            index._scans[archive] = scan;
            progress?.Report(new IndexProgress(Interlocked.Increment(ref done), archives.Length, archive.ToString()));
        });
        index.rebuildFamilies();
        index.saveCache();
        return index;
    }

    /// <summary>Rescans one archive (after it was written) and recomputes clone families.</summary>
    public void Refresh(ArchiveId archive)
    {
        var scan = this.scan(archive, stamp(_store.SourcePath(archive)), stamp(_store.CurrentPath(archive)), new(StringComparer.Ordinal));
        _scans[archive] = scan;
        rebuildFamilies();
        saveCache();
    }

    public IEnumerable<(ArchiveId Archive, string Error)> Errors =>
        _scans.Where(p => p.Value.Error is not null).Select(p => (p.Key, p.Value.Error!));

    public TextureEntry? Get(TextureId id) => _entries.GetValueOrDefault(id);

    public IEnumerable<TextureEntry> InArchive(ArchiveId archive) =>
        _scans.TryGetValue(archive, out var scan) ? scan.Textures.Select(t => _entries[new TextureId(archive, t.Index)]) : [];

    /// <summary>The clone family key: the original pixel hash shared by every clone.</summary>
    public string FamilyKey(TextureEntry entry) =>
        entry.Id.Archive.Layer == StudioProject.MainLayer || _originals.Contains(entry.SourceHash)
            ? entry.SourceHash : _currentToFamily.GetValueOrDefault(entry.SourceHash, entry.SourceHash);

    /// <summary>Every texture with the same original content, including <paramref name="id"/> itself.</summary>
    public IReadOnlyList<TextureEntry> Family(TextureId id) =>
        Get(id) is { } entry && _families.TryGetValue(FamilyKey(entry), out var members) ? members : Get(id) is { } single ? [single] : [];

    /// <summary>
    /// Whether the texture carries a real edit of its family's original: its own significant modification, or
    /// (in a linked layer) content identical to a significantly modified main texture.
    /// </summary>
    public bool IsEdited(TextureEntry entry) =>
        entry.Modified ? entry.SignificantlyModified
            : FamilyKey(entry) != entry.SourceHash && _significantCurrent.Contains(entry.SourceHash);

    public IEnumerable<IReadOnlyList<TextureEntry>> Families => _families.Values.Where(f => f.Count > 1);

    private ArchiveScan scan(ArchiveId archive, string sourceStamp, string currentStamp, ConcurrentDictionary<string, (string, bool)> memo)
    {
        var source = _store.ReadSource(archive);
        var current = _store.IsModified(archive) ? _store.ReadCurrent(archive) : source;
        var kind = ArchiveStore.Kind(archive);
        var packList = kind == ArchiveKind.Ddp ? _store.PackList(archive) : null;
        var slots = TextureContainers.Slots(source, kind, Path.GetFileName(archive.Path), packList);
        var sourceTextures = TextureContainers.Textures(source, kind);
        var currentTextures = ReferenceEquals(source, current) ? sourceTextures : TextureContainers.Textures(current, kind);
        var result = new ArchiveScan { SourceStamp = sourceStamp, CurrentStamp = currentStamp };
        foreach (var slot in slots)
        {
            {
                var global = slot.Index;
                var original = describe(sourceTextures[global], memo);
                var now = ReferenceEquals(source, current) || global >= currentTextures.Count ? original : describe(currentTextures[global], memo);
                result.Textures.Add(new TextureScan
                {
                    Index = global, Movie = slot.Group, Local = slot.Local,
                    Name = slot.Name,
                    Width = original.Width, Height = original.Height, Format = (byte)original.Format, SourceHash = original.Hash,
                    CurrentHash = now.Hash, CurrentWidth = now.Width, CurrentHeight = now.Height, CurrentFormat = (byte)now.Format,
                    Uniform = original.Uniform,
                    ChangedPixels = original.Hash == now.Hash ? 0
                        : original.Width != now.Width || original.Height != now.Height ? -1
                        : ChangedPixelCount(NutTextureDecoder.DecodeRgba8(sourceTextures[global]), NutTextureDecoder.DecodeRgba8(currentTextures[global])),
                });
            }
        }
        return result;
    }

    private static (string Hash, int Width, int Height, NutPixelFormat Format, bool Uniform) describe(NutTexture texture,
        ConcurrentDictionary<string, (string Hash, bool Uniform)> memo)
    {
        // Identical payloads decode identically: hash the payload first and decode each distinct one once.
        var payloadKey = $"{(byte)texture.Format}:{texture.Width}x{texture.Height}:" + Convert.ToHexString(SHA256.HashData(texture.Data.Span));
        var (hash, uniform) = memo.GetOrAdd(payloadKey, _ =>
        {
            var rgba = NutTextureDecoder.DecodeRgba8(texture);
            return (PixelHash(texture.Width, texture.Height, rgba), IsUniform(rgba));
        });
        return (hash, texture.Width, texture.Height, texture.Format, uniform);
    }

    /// <summary>
    /// Texels whose visible (premultiplied) colour or alpha differs by more than <see cref="VisibleDifference"/>:
    /// re-encoding noise stays below it, real edits (text, art) exceed it.
    /// </summary>
    public static int ChangedPixelCount(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var count = 0;
        for (var i = 0; i + 3 < a.Length && i + 3 < b.Length; i += 4)
        {
            var difference = Math.Abs(a[i + 3] - b[i + 3]);
            for (var c = 0; c < 3; c++)
                difference = Math.Max(difference, Math.Abs(a[i + c] * a[i + 3] - b[i + c] * b[i + 3]) / 255);
            if (difference > VisibleDifference) count++;
        }
        return count;
    }

    public const int VisibleDifference = 40;

    /// <summary>Content identity of an image: its dimensions and R,G,B,A bytes.</summary>
    public static string PixelHash(int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BitConverter.TryWriteBytes(size, width); BitConverter.TryWriteBytes(size[4..], height);
        hash.AppendData(size); hash.AppendData(rgba);
        return Convert.ToHexString(hash.GetHashAndReset())[..32];
    }

    public static bool IsUniform(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < 8) return true;
        var first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rgba);
        // Fully transparent texels count as equal whatever colour they store.
        var transparent = rgba[3] == 0;
        for (var i = 4; i < rgba.Length; i += 4)
        {
            if (transparent && rgba[i + 3] == 0) continue;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rgba[i..]) != first) return false;
        }
        return true;
    }

    private void rebuildFamilies()
    {
        var entries = new Dictionary<TextureId, TextureEntry>();
        foreach (var (archive, scan) in _scans)
            foreach (var t in scan.Textures)
            {
                var id = new TextureId(archive, t.Index);
                entries[id] = new TextureEntry(id, t.Movie, t.Local, t.Name, t.Width, t.Height, (NutPixelFormat)t.Format,
                    t.SourceHash, t.CurrentHash, t.CurrentWidth, t.CurrentHeight, (NutPixelFormat)t.CurrentFormat, t.Uniform, t.ChangedPixels);
            }
        _entries = entries;
        var main = entries.Values.Where(e => e.Id.Archive.Layer == StudioProject.MainLayer).ToArray();
        var originals = _originals = main.Select(e => e.SourceHash).ToHashSet(StringComparer.Ordinal);
        // Modified content maps back to the original it replaced; ambiguous content picks its most common original.
        _currentToFamily = main.Where(e => e.Modified && !originals.Contains(e.CurrentHash))
            .GroupBy(e => e.CurrentHash)
            .ToDictionary(g => g.Key, g => g.GroupBy(e => e.SourceHash).OrderByDescending(s => s.Count()).First().Key, StringComparer.Ordinal);
        _significantCurrent = main.Where(e => e.SignificantlyModified).Select(e => e.CurrentHash).ToHashSet(StringComparer.Ordinal);
        var families = new Dictionary<string, List<TextureEntry>>(StringComparer.Ordinal);
        foreach (var entry in entries.Values.OrderBy(e => e.Id.Archive.Layer, StringComparer.Ordinal).ThenBy(e => e.Id.Archive.Path, StringComparer.Ordinal).ThenBy(e => e.Id.Index))
        {
            var key = FamilyKey(entry);
            if (!families.TryGetValue(key, out var list)) families[key] = list = [];
            list.Add(entry);
        }
        _families = families;
    }

    private static string archiveKey(ArchiveId id) => id.Layer + "|" + id.Path;

    private static string stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private Dictionary<string, ArchiveScan> loadCache()
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return [];
        try
        {
            var cache = JsonSerializer.Deserialize(File.ReadAllBytes(_cachePath), StudioJson.Default.TextureIndexCache);
            return cache?.Version == CacheVersion ? cache.Archives : [];
        }
        catch (JsonException) { return []; }
    }

    private void saveCache()
    {
        if (_cachePath is null) return;
        var cache = new TextureIndexCache { Version = CacheVersion, Archives = _scans.ToDictionary(p => archiveKey(p.Key), p => p.Value) };
        AtomicFile.Write(_cachePath, JsonSerializer.SerializeToUtf8Bytes(cache, StudioJson.Default.TextureIndexCache));
    }
}

public readonly record struct IndexProgress(int Done, int Total, string Current);

internal sealed class TextureIndexCache
{
    public int Version { get; set; }
    public Dictionary<string, ArchiveScan> Archives { get; set; } = [];
}

internal sealed class ArchiveScan
{
    public string SourceStamp { get; set; } = "";
    public string CurrentStamp { get; set; } = "";
    public string? Error { get; set; }
    public List<TextureScan> Textures { get; set; } = [];
}

internal sealed class TextureScan
{
    public int Index { get; set; }
    public string Movie { get; set; } = "";
    public int Local { get; set; }
    public string? Name { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte Format { get; set; }
    public string SourceHash { get; set; } = "";
    public string CurrentHash { get; set; } = "";
    public int CurrentWidth { get; set; }
    public int CurrentHeight { get; set; }
    public byte CurrentFormat { get; set; }
    public bool Uniform { get; set; }
    /// <summary>Visibly changed texels versus the source; -1 when the size changed.</summary>
    public int ChangedPixels { get; set; }
}
