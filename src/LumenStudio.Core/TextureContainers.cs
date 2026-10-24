using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;

namespace LumenStudio.Core;

public enum ArchiveKind
{
    /// <summary><c>LM_NUT_TYPE1</c>: Lumen movies with their textures.</summary>
    Ddp,
    /// <summary><c>NUT_PACK_TYPE1</c>: a named list of NUTs.</summary>
    Ndp,
    /// <summary>A standalone NTP3 file with one or more textures.</summary>
    Nut,
}

/// <summary>
/// One texture inside an archive. <see cref="Group"/> is the DDP movie, the NDP entry name or the NUT
/// file name; <see cref="Local"/> the texture's index within that group (the movie-local texture index).
/// </summary>
public sealed record TextureSlot(int Index, string Group, int Local, string? Name);

/// <summary>Uniform texture access across DDP, NDP and NUT archives (flat texture indices).</summary>
public static class TextureContainers
{
    public static ArchiveKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ndp" => ArchiveKind.Ndp,
        ".nut" => ArchiveKind.Nut,
        _ => ArchiveKind.Ddp,
    };

    public static ArchiveKind KindOf(ArchiveId id) => KindOf(id.Path);

    public static IReadOnlyList<TextureSlot> Slots(byte[] archive, ArchiveKind kind, string fileName, PackList? names = null)
    {
        var slots = new List<TextureSlot>();
        switch (kind)
        {
            case ArchiveKind.Ddp:
                var ddp = DdpArchive.Open(archive);
                foreach (var movie in ddp.Index.Movies)
                    for (var global = movie.TextureBegin; global < movie.TextureEnd; global++)
                        slots.Add(new TextureSlot(global, movie.Name, global - movie.TextureBegin, names?.TextureName(movie.Name, global - movie.TextureBegin)));
                break;
            case ArchiveKind.Ndp:
                var ndp = NdpArchive.Parse(archive);
                foreach (var entry in ndp.Entries)
                {
                    var count = NutFile.Parse(ndp.Read(archive, entry.Index)).Textures.Length;
                    for (var t = 0; t < count; t++)
                        slots.Add(new TextureSlot(slots.Count, entry.Name, t, count == 1 ? entry.Name : $"{entry.Name}#{t}"));
                }
                break;
            case ArchiveKind.Nut:
                var nut = NutFile.Parse(archive);
                for (var t = 0; t < nut.Textures.Length; t++)
                    slots.Add(new TextureSlot(t, fileName, t, nut.Textures.Length == 1 ? fileName : $"{fileName}#{t}"));
                break;
        }
        return slots;
    }

    /// <summary>Zero-copy texture views in flat index order (for hashing and decoding).</summary>
    public static IReadOnlyList<NutTexture> Textures(byte[] archive, ArchiveKind kind)
    {
        switch (kind)
        {
            case ArchiveKind.Ddp:
                return DdpArchive.Open(archive).Index.Textures
                    .Select(e => NutFile.Parse(archive.AsMemory(checked((int)e.Offset), checked((int)e.Length))).Textures[0]).ToArray();
            case ArchiveKind.Ndp:
                var ndp = NdpArchive.Parse(archive);
                return ndp.Entries.SelectMany(e => NutFile.Parse(ndp.Read(archive, e.Index)).Textures).ToArray();
            default:
                return NutFile.Parse(archive).Textures;
        }
    }

    /// <summary>Texture <paramref name="index"/> as a single-texture NTP3 v1 file.</summary>
    public static byte[] ReadTexture(byte[] archive, ArchiveKind kind, int index)
    {
        switch (kind)
        {
            case ArchiveKind.Ddp:
            {
                var entry = DdpArchive.Open(archive).Index.Textures[index];
                return NutPack.Extract(archive.AsMemory(checked((int)entry.Offset), checked((int)entry.Length)), 0);
            }
            case ArchiveKind.Ndp:
            {
                var (entry, sub, ndp) = locate(archive, index);
                return NutPack.Extract(ndp.Read(archive, entry), sub);
            }
            default:
                return NutPack.Extract(archive, index);
        }
    }

    /// <summary>Replaces textures (single-texture NUTs by flat index); DDP movie edits are applied by <see cref="ArchiveBuilder"/>.</summary>
    public static byte[] ReplaceTextures(byte[] archive, ArchiveKind kind, IReadOnlyDictionary<int, byte[]> textures,
        IReadOnlyDictionary<string, byte[]>? movies = null)
    {
        switch (kind)
        {
            case ArchiveKind.Ddp:
                return DdpArchiveWriter.Rebuild(archive, movies, textures);
            case ArchiveKind.Ndp:
            {
                if (textures.Count == 0) return archive;
                var ndp = NdpArchive.Parse(archive);
                var byEntry = new Dictionary<int, Dictionary<int, byte[]>>();
                foreach (var (index, nut) in textures)
                {
                    var (entry, sub, _) = locate(archive, index, ndp);
                    if (!byEntry.TryGetValue(entry, out var subs)) byEntry[entry] = subs = [];
                    subs[sub] = nut;
                }
                var entries = byEntry.ToDictionary(p => p.Key, p => NutPack.Rebuild(ndp.Read(archive, p.Key), p.Value));
                return NdpArchive.Rebuild(archive, entries);
            }
            default:
                return textures.Count == 0 ? archive : NutPack.Rebuild(archive, textures);
        }
    }

    private static (int Entry, int Sub, NdpArchive Archive) locate(byte[] archive, int index, NdpArchive? parsed = null)
    {
        var ndp = parsed ?? NdpArchive.Parse(archive);
        var flat = 0;
        foreach (var entry in ndp.Entries)
        {
            var count = NutFile.Parse(ndp.Read(archive, entry.Index)).Textures.Length;
            if (index < flat + count) return (entry.Index, index - flat, ndp);
            flat += count;
        }
        throw new ArgumentOutOfRangeException(nameof(index), $"Texture {index} is outside the archive.");
    }
}
