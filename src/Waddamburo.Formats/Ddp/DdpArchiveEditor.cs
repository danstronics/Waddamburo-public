using System.Buffers.Binary;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Ddp;

/// <summary>Replaces movie payloads without rebuilding opaque directory metadata or NUT blocks.</summary>
public static class DdpArchiveEditor
{
    public static byte[] ReplaceMovie(ReadOnlyMemory<byte> source, string name, ReadOnlyMemory<byte> movieData, bool preserveLayout)
    {
        var archive = DdpArchive.Open(source);
        var movie = archive.Index.GetMovie(name);
        var original = archive.OpenMovie(movie);
        var semantic = LmbSemanticReader.Read(LmbFile.Parse(movieData), validationContext: new LmbSemanticValidationContext(original.Textures.Length));
        if (semantic.Diagnostics.Any(d => d.Severity == Diagnostics.DiagnosticSeverity.Error))
            throw new InvalidDataException("Edited movie contains invalid references.");
        if (original.Data.Span.SequenceEqual(movieData.Span)) return source.ToArray();
        if (preserveLayout)
        {
            var document = new LmbEditDocument(original.Data.Span);
            if (movieData.Length != original.Data.Length) throw new InvalidOperationException("Conservative export requires the original movie length.");
            var oldRecords = document.Definition.RawFile.Records;
            var newRecords = semantic.Value.RawFile.Records;
            if (oldRecords.Length != newRecords.Length || !oldRecords.Zip(newRecords).All(p => p.First.Tag == p.Second.Tag && p.First.WordCount == p.Second.WordCount)
                || !document.Definition.RawFile.HeaderBytes.SequenceEqual(semantic.Value.RawFile.HeaderBytes))
                throw new InvalidOperationException("Conservative export requires unchanged record layout and header.");
        }
        var start = checked((int)movie.Offset);
        var end = checked(start + (int)movie.Length);
        foreach (var other in archive.Index.Movies.Where(m => m.Index != movie.Index))
            if (other.Offset < end && other.Offset + other.Length > start)
                throw new InvalidDataException("Overlapping movie payloads cannot be edited safely.");
        // In rebuilt mode, preserve every old gap and all following bytes. A 4096-byte multiple
        // preserves the existing alignment of following sections up to that boundary.
        var growth = Math.Max(0, checked(movieData.Length - (int)movie.Length));
        var shift = checked((growth + 4095) & ~4095);
        var output = new byte[checked(source.Length + shift)];
        source.Span[..start].CopyTo(output);
        movieData.Span.CopyTo(output.AsSpan(start));
        source.Span[end..].CopyTo(output.AsSpan(end + shift));
        if (movieData.Length < movie.Length)
            source.Span.Slice(start + movieData.Length, (int)movie.Length - movieData.Length).CopyTo(output.AsSpan(start + movieData.Length));
        var cursor = checked(44 + (int)read(source.Span, 16) + 9);
        foreach (var entry in archive.Index.Movies)
        {
            var nameLength = checked((int)read(source.Span, cursor));
            cursor = checked(cursor + 4 + nameLength + (entry.Index == 0 ? 5 : 0));
            if (entry.Index == movie.Index) write(output, cursor + 4, (uint)movieData.Length);
            else if (entry.Offset >= end) write(output, cursor, checked(entry.RelativeOffset + (uint)shift));
            cursor += 16;
        }
        write(output, checked((int)archive.Index.MovieDataOffset - 16), checked((uint)archive.Index.MovieDataLength + (uint)shift));
        var rebuilt = DdpArchive.Open(output);
        if (!rebuilt.OpenMovie(name).Data.Span.SequenceEqual(movieData.Span)) throw new InvalidDataException("Movie export verification failed.");
        if (!source.Span.Slice((int)archive.Index.TextureDataOffset, (int)archive.Index.TextureDataLength)
            .SequenceEqual(output.AsSpan((int)rebuilt.Index.TextureDataOffset, (int)rebuilt.Index.TextureDataLength)))
            throw new InvalidDataException("Texture allocation changed during movie export.");
        foreach (var entry in archive.Index.Movies.Where(m => m.Index != movie.Index))
            if (!archive.OpenMovie(entry).Data.Span.SequenceEqual(rebuilt.OpenMovie(entry.Name).Data.Span))
                throw new InvalidDataException("An unrelated movie changed during export.");
        return output;
    }

    /// <summary>Changes pixels only. Rejects dimension, format, mip or allocation changes.</summary>
    public static byte[] ReplaceTexture(ReadOnlyMemory<byte> source, int index, ReadOnlyMemory<byte> replacement)
    {
        var archive = DdpArchive.Open(source);
        var entry = archive.Index.Textures[index];
        var original = source.Slice((int)entry.Offset, (int)entry.Length);
        var oldPack = NutFile.Parse(original);
        var newPack = NutFile.Parse(replacement);
        if (oldPack.Textures.Length != newPack.Textures.Length) throw new InvalidOperationException("Texture count must remain unchanged.");
        var output = source.ToArray();
        foreach (var pair in oldPack.Textures.Zip(newPack.Textures))
        {
            var a = pair.First;
            var b = pair.Second;
            if (a.Width != b.Width || a.Height != b.Height || a.Format != b.Format || a.MipCount != b.MipCount || a.DataSize != b.DataSize)
                throw new InvalidOperationException("Texture dimensions, format, mip count and payload length must match exactly.");
            b.Data.Span.CopyTo(output.AsSpan(checked((int)(entry.Offset + a.DataOffset)), (int)a.DataSize));
        }
        _ = DdpArchive.Open(output);
        return output;
    }

    private static uint read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
    private static void write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
}
