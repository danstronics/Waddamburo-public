using System.Buffers.Binary;
using System.Text;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;

namespace LumenStudio.Authoring.Packaging;

/// <summary>One movie of an archive and the NUT textures its LMB indexes (0 = its first).</summary>
public sealed record ArchiveMovie(string Name, byte[] Lmb, IReadOnlyList<byte[]> Textures);

/// <summary>
/// Writes a Green <c>LM_NUT_TYPE1</c> archive from scratch: the boost serialization preamble, the
/// movie directory (name, offset, length, texture span), the texture table, the trailer (movie block
/// length, texture block length, largest texture entry, texture count), then both payload blocks.
/// Texture entries are 16-byte aligned and contiguous, as observed in every shipped archive.
/// </summary>
public static class ArchiveWriter
{
    // "LM_NUT_TYPE1", a zero word, then boost::archive's "serialization::archive" signature and version block.
    private static readonly byte[] Preamble = Convert.FromHexString(
        "4C4D5F4E55545F5459504531000000000000001673657269616C697A6174696F6E3A3A61726368697665000A040404080000000100000000000000000000");

    public static byte[] Write(IReadOnlyList<ArchiveMovie> movies)
    {
        ArgumentNullException.ThrowIfNull(movies);
        if (movies.Count == 0) throw new ArgumentException("An archive holds at least one movie.", nameof(movies));
        var textures = new List<byte[]>();
        var spans = new List<(int Begin, int End)>();
        foreach (var movie in movies)
        {
            var begin = textures.Count;
            foreach (var texture in movie.Textures)
            {
                if (NutFile.Parse(texture).Textures.Length != 1) throw new InvalidDataException($"{movie.Name}: each texture entry holds one NTP3 texture.");
                textures.Add(pad16(texture));
            }
            spans.Add((begin, textures.Count));
        }
        using var output = new MemoryStream();
        output.Write(Preamble);
        writeU32(output, (uint)movies.Count);
        output.Write(new byte[9]);
        uint movieOffset = 0;
        for (var i = 0; i < movies.Count; i++)
        {
            var name = Encoding.UTF8.GetBytes(movies[i].Name);
            writeU32(output, (uint)name.Length);
            output.Write(name);
            if (i == 0) output.Write(new byte[5]);
            writeU32(output, movieOffset);
            writeU32(output, (uint)movies[i].Lmb.Length);
            writeU32(output, (uint)spans[i].Begin);
            writeU32(output, (uint)spans[i].End);
            movieOffset = checked(movieOffset + (uint)movies[i].Lmb.Length);
        }
        output.Write(new byte[5]);
        writeU32(output, (uint)textures.Count);
        output.Write(new byte[9]);
        uint textureOffset = 0;
        uint largest = 0;
        foreach (var texture in textures)
        {
            writeU32(output, textureOffset);
            writeU32(output, (uint)texture.Length);
            textureOffset = checked(textureOffset + (uint)texture.Length);
            largest = Math.Max(largest, (uint)texture.Length);
        }
        writeU32(output, movieOffset);
        writeU32(output, textureOffset);
        writeU32(output, largest);
        writeU32(output, (uint)textures.Count);
        foreach (var movie in movies) output.Write(movie.Lmb);
        foreach (var texture in textures) output.Write(texture);
        var bytes = output.ToArray();
        verify(bytes, movies);
        return bytes;
    }

    private static void verify(byte[] bytes, IReadOnlyList<ArchiveMovie> movies)
    {
        var archive = DdpArchive.Open(bytes);
        if (archive.Index.Movies.Length != movies.Count) throw new InvalidDataException("Archive verification failed (movie count).");
        foreach (var movie in movies)
        {
            var view = archive.OpenMovie(movie.Name);
            if (!view.Data.Span.SequenceEqual(movie.Lmb) || view.Textures.Length != movie.Textures.Count)
                throw new InvalidDataException($"Archive verification failed for {movie.Name}.");
        }
    }

    private static byte[] pad16(byte[] data)
    {
        if ((data.Length & 15) == 0) return data;
        var padded = new byte[(data.Length + 15) & ~15];
        data.CopyTo(padded, 0);
        return padded;
    }

    private static void writeU32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }
}

/// <summary>Single-texture NTP3 v1 files (Green's layout) made from RGBA pixels.</summary>
public static class NutBuilder
{
    // NTP3 v1 with one texture; a 0x50 texture header (sizes, one mip, format, 32×32, eXt and GIDX blocks).
    private static readonly byte[] Template = build();

    public static byte[] FromRgba(int width, int height, ReadOnlySpan<byte> rgba, NutPixelFormat format = NutPixelFormat.Bc3) =>
        NutTextureWriter.Write(Template, width, height, rgba, format);

    private static byte[] build()
    {
        var header = Convert.FromHexString(
            "4E5450330100000100000000000000000000045000000000000004000050000000010002002000200000000000000000000000000000000000000000000000006558740000000020000000100000000047494458000000100000000000000000");
        var bytes = new byte[0x10 + 0x50 + 0x400];
        header.AsSpan(0, Math.Min(header.Length, 0x60)).CopyTo(bytes);
        return bytes;
    }
}
