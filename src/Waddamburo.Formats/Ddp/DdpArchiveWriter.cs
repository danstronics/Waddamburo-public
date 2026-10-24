using System.Buffers.Binary;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Ddp;

/// <summary>
/// Rebuilds a Green DDP with replaced movies and textures of any length. The directory bytes,
/// movie gaps and every untouched payload are retained; the texture block is relaid contiguously
/// at 16-byte granularity (the observed layout) and the trailer's movie length, texture length and
/// largest-texture-entry word (the original executable's load buffer size) are recomputed.
/// </summary>
public static class DdpArchiveWriter
{
    public static byte[] Rebuild(ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, byte[]>? movies = null,
        IReadOnlyDictionary<int, byte[]>? textures = null)
    {
        var bytes = source.ToArray();
        if (movies is not null)
            foreach (var (name, data) in movies)
                bytes = DdpArchiveEditor.ReplaceMovie(bytes, name, data, preserveLayout: false);
        if (textures is not null && textures.Count != 0)
            bytes = replaceTextures(bytes, textures);
        verify(source, bytes, movies, textures);
        return bytes;
    }

    private static byte[] replaceTextures(byte[] source, IReadOnlyDictionary<int, byte[]> replacements)
    {
        var archive = DdpArchive.Open(source);
        var index = archive.Index;
        foreach (var (textureIndex, nut) in replacements)
        {
            if ((uint)textureIndex >= (uint)index.Textures.Length) throw new ArgumentOutOfRangeException(nameof(replacements), $"Texture {textureIndex} is outside the archive.");
            var pack = NutFile.Parse(nut);
            if (pack.Textures.Length != 1) throw new InvalidDataException($"Texture {textureIndex}: a DDP entry must hold exactly one NTP3 texture.");
        }
        var entries = new byte[index.Textures.Length][];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = index.Textures[i];
            var data = replacements.TryGetValue(i, out var replacement) ? replacement
                : source.AsSpan(checked((int)entry.Offset), checked((int)entry.Length)).ToArray();
            if ((data.Length & 15) != 0)
            {
                var padded = new byte[(data.Length + 15) & ~15];
                data.CopyTo(padded, 0); data = padded;
            }
            entries[i] = data;
        }
        // Untouched entries keep their original relative order and bytes; any original gaps are closed.
        var textureBlockLength = entries.Sum(static e => (long)e.Length);
        var textureStart = checked((int)index.TextureDataOffset);
        var oldPayloadEnd = checked((int)(index.TextureDataOffset + index.TextureDataLength));
        var output = new byte[checked(textureStart + textureBlockLength + (source.Length - oldPayloadEnd))];
        source.AsSpan(0, textureStart).CopyTo(output);
        var tableStart = checked((int)index.MovieDataOffset - 16 - 8 * index.Textures.Length);
        long relative = 0; long largest = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i].CopyTo(output, checked((int)(textureStart + relative)));
            write(output, tableStart + i * 8, checked((uint)relative));
            write(output, tableStart + i * 8 + 4, checked((uint)entries[i].Length));
            relative += entries[i].Length;
            largest = Math.Max(largest, entries[i].Length);
        }
        source.AsSpan(oldPayloadEnd).CopyTo(output.AsSpan(checked((int)(textureStart + textureBlockLength))));
        var trailer = checked((int)index.MovieDataOffset - 16);
        write(output, trailer + 4, checked((uint)textureBlockLength));
        write(output, trailer + 8, checked((uint)largest));
        return output;
    }

    private static void verify(ReadOnlyMemory<byte> original, byte[] output,
        IReadOnlyDictionary<string, byte[]>? movies, IReadOnlyDictionary<int, byte[]>? textures)
    {
        var before = DdpArchive.Open(original);
        var after = DdpArchive.Open(output);
        if (before.Index.Movies.Length != after.Index.Movies.Length || before.Index.Textures.Length != after.Index.Textures.Length)
            throw new InvalidDataException("DDP rebuild changed the directory size.");
        foreach (var movie in before.Index.Movies)
        {
            var expected = movies is not null && movies.TryGetValue(movie.Name, out var replaced) ? replaced : before.OpenMovie(movie).Data.ToArray();
            var actual = after.OpenMovie(movie.Name);
            if (!actual.Data.Span.SequenceEqual(expected)) throw new InvalidDataException($"DDP rebuild verification failed for movie {movie.Name}.");
            if (actual.Entry.TextureBegin != movie.TextureBegin || actual.Entry.TextureEnd != movie.TextureEnd)
                throw new InvalidDataException($"DDP rebuild changed the texture range of {movie.Name}.");
        }
        long largest = 0;
        for (var i = 0; i < before.Index.Textures.Length; i++)
        {
            var entry = after.Index.Textures[i];
            largest = Math.Max(largest, entry.Length);
            var actual = output.AsMemory(checked((int)entry.Offset), checked((int)entry.Length));
            var expected = textures is not null && textures.TryGetValue(i, out var replaced) ? replaced
                : original.Slice(checked((int)before.Index.Textures[i].Offset), checked((int)before.Index.Textures[i].Length)).ToArray();
            if (!actual.Span[..expected.Length].SequenceEqual(expected)) throw new InvalidDataException($"DDP rebuild verification failed for texture {i}.");
            _ = NutFile.Parse(actual);
        }
        if (textures is { Count: > 0 } && after.Index.TrailerUnknown != largest)
            throw new InvalidDataException("DDP rebuild left a stale largest-texture trailer word.");
        if (after.Index.TrailerTextureCount != before.Index.TrailerTextureCount)
            throw new InvalidDataException("DDP rebuild changed the trailer texture count.");
    }

    private static void write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
}
