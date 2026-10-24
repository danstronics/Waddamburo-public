using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;

namespace LumenStudio.Core;

/// <summary>Applies pending movie and texture edits to an archive's current bytes.</summary>
public static class ArchiveBuilder
{
    public static byte[] Build(ArchiveStore store, ArchiveId archive, ChangeSet changes)
    {
        var layer = store.Project.Layer(archive.Layer);
        var bytes = store.ReadCurrent(archive);
        var ddp = DdpArchive.Open(bytes);
        var movies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var ((owner, movie), data) in changes.Movies)
        {
            if (owner != archive) continue;
            if (layer.ProtectMovies) throw new InvalidOperationException($"{archive}: movies in layer '{layer.Name}' are protected; only texture pixels may change.");
            movies[movie] = data;
        }
        var documents = new Dictionary<string, LmbEditDocument>(StringComparer.Ordinal);
        var textures = new Dictionary<int, byte[]>();
        foreach (var (id, pending) in changes.Textures)
        {
            if (id.Archive != archive) continue;
            var entry = ddp.Index.Textures[id.Index];
            var template = bytes.AsMemory(checked((int)entry.Offset), checked((int)entry.Length));
            var original = NutFile.Parse(template).Textures[0];
            var image = pending.Image;
            if (!layer.AllowResize && (image.Width != original.Width || image.Height != original.Height))
                image = image.Resize(original.Width, original.Height);
            byte[] nut;
            if (pending.EncodedNut is { } exact && NutFile.Parse(exact).Textures[0] is var copied
                && copied.Format == original.Format && copied.Width == image.Width && copied.Height == image.Height)
                nut = exact;
            else
                nut = NutTextureWriter.Write(template, image.Width, image.Height, image.Rgba, original.Format);
            textures[id.Index] = nut;
            if (image.Width == original.Width && image.Height == original.Height) continue;
            // Resized: keep the owning movie's F007 texture size in step with the NUT.
            var owner = ddp.Index.Movies.First(m => m.TextureBegin <= id.Index && id.Index < m.TextureEnd);
            if (!documents.TryGetValue(owner.Name, out var document))
                documents[owner.Name] = document = new LmbEditDocument(movies.TryGetValue(owner.Name, out var edited) ? edited : ddp.OpenMovie(owner).Data.Span);
            document.SetTextureSize(id.Index - owner.TextureBegin, image.Width, image.Height);
        }
        foreach (var (name, document) in documents)
            movies[name] = document.Export(preserveLayout: document.PreservesRecordLayout);
        return DdpArchiveWriter.Rebuild(bytes, movies, textures);
    }
}

public enum IssueSeverity { Info, Warning, Error }

public sealed record CompatibilityIssue(IssueSeverity Severity, string Message);

/// <summary>
/// Checks an output archive against the invariants observed across the whole Green corpus, which the
/// original PS3 / System 357 executable relies on (see docs/lumenstudio/format-findings.md).
/// </summary>
public static class CompatibilityChecker
{
    public static IReadOnlyList<CompatibilityIssue> Check(ReadOnlyMemory<byte> output, ReadOnlyMemory<byte> source)
    {
        var issues = new List<CompatibilityIssue>();
        void error(string message) => issues.Add(new(IssueSeverity.Error, message));
        void warn(string message) => issues.Add(new(IssueSeverity.Warning, message));
        DdpArchive after, before;
        try { after = DdpArchive.Open(output); before = DdpArchive.Open(source); }
        catch (Exception exception) { error("The archive does not parse: " + exception.Message); return issues; }
        if (after.Index.Movies.Length != before.Index.Movies.Length) error("The movie count changed.");
        if (after.Index.Textures.Length != before.Index.Textures.Length) error("The texture count changed.");
        if (after.Index.TrailerTextureCount != before.Index.TrailerTextureCount) error("The trailer texture count changed.");
        long largest = 0, previousEnd = 0;
        foreach (var entry in after.Index.Textures)
        {
            largest = Math.Max(largest, entry.Length);
            if (entry.RelativeOffset % 16 != 0 || entry.Length % 16 != 0) error($"Texture {entry.Index} is not 16-byte aligned.");
            if (entry.RelativeOffset != previousEnd) warn($"Texture {entry.Index} does not follow the previous texture contiguously.");
            previousEnd = entry.RelativeOffset + entry.Length;
            NutFile nut;
            try { nut = NutFile.Parse(output.Slice(checked((int)entry.Offset), checked((int)entry.Length))); }
            catch (Exception exception) { error($"Texture {entry.Index} does not parse: {exception.Message}"); continue; }
            if (nut.Version != 1 || nut.Textures.Length != 1) { error($"Texture {entry.Index} is not a single-texture NTP3 v1."); continue; }
            var t = nut.Textures[0];
            var expected = NutTextureWriter.EntryLength(t.Format, t.Width, t.Height, t.HeaderSize);
            if (t.MipCount == 1 && entry.Length != expected) error($"Texture {entry.Index}: entry length {entry.Length} differs from the Green layout ({expected}).");
            if (t.TotalSize != t.HeaderSize + t.DataSize) error($"Texture {entry.Index}: total size does not equal header + data.");
            if (t.Width > NutTextureWriter.MaxDimension || t.Height > NutTextureWriter.MaxDimension) error($"Texture {entry.Index} exceeds {NutTextureWriter.MaxDimension} pixels.");
            if (entry.Index < before.Index.Textures.Length)
            {
                var o = NutFile.Parse(source.Slice(checked((int)before.Index.Textures[entry.Index].Offset), checked((int)before.Index.Textures[entry.Index].Length))).Textures[0];
                if (o.Format != t.Format) warn($"Texture {entry.Index} changed format ({o.Format} → {t.Format}).");
                if (o.HeaderSize != t.HeaderSize) error($"Texture {entry.Index} changed its header size.");
            }
        }
        if (after.Index.TrailerUnknown != largest)
            error($"Trailer load-buffer word is {after.Index.TrailerUnknown}, the largest texture entry is {largest}: the original executable would overflow.");
        foreach (var movie in after.Index.Movies)
        {
            var old = before.Index.Movies[movie.Index];
            if (old.Name != movie.Name || old.TextureBegin != movie.TextureBegin || old.TextureEnd != movie.TextureEnd)
                error($"Movie {movie.Name}: name or texture range changed.");
            var view = after.OpenMovie(movie);
            var read = LmbSemanticReader.Read(LmbFile.Parse(view.Data), validationContext: new LmbSemanticValidationContext(view.Textures.Length));
            foreach (var diagnostic in read.Diagnostics.Where(d => d.Severity == Waddamburo.Formats.Diagnostics.DiagnosticSeverity.Error))
                error($"Movie {movie.Name}: {diagnostic.Code} {diagnostic.Message}");
            for (var local = 0; local < view.Textures.Length; local++)
            {
                var t = NutFile.Parse(view.Textures[local].Data).Textures[0];
                var recorded = read.Value.TextureBounds.FirstOrDefault(b => BitConverter.SingleToUInt32Bits(b.Left) == (uint)local);
                if (recorded is not null && (recorded.Right != t.Width || recorded.Bottom != t.Height))
                    error($"Movie {movie.Name}: F007 says texture {local} is {recorded.Right}×{recorded.Bottom}, the NUT is {t.Width}×{t.Height}.");
            }
        }
        var growth = (double)after.Index.TextureDataLength / Math.Max(1, before.Index.TextureDataLength);
        if (growth > 2) warn($"Texture data grew {growth:0.0}× ({before.Index.TextureDataLength / 1048576.0:0.0} → {after.Index.TextureDataLength / 1048576.0:0.0} MiB): check memory on hardware.");
        if (after.Index.TrailerUnknown > before.Index.TrailerUnknown * 4L && after.Index.TrailerUnknown > 4 * 1048576)
            warn($"The largest texture grew to {after.Index.TrailerUnknown / 1048576.0:0.0} MiB.");
        return issues;
    }
}
