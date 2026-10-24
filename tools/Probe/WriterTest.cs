using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;
static class WriterTest
{
    static double psnr(byte[] a, byte[] b)
    {
        double se = 0; int n = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] < 16 && b[i + 3] < 16) { se += Math.Pow(a[i + 3] - b[i + 3], 2); n++; continue; }
            for (var c = 0; c < 4; c++) { se += Math.Pow(a[i + c] - b[i + c], 2); n++; }
        }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / (se / n));
    }
    public static void Run(string ddp)
    {
        var bytes = File.ReadAllBytes(ddp);
        var same = DdpArchiveWriter.Rebuild(bytes, null, new Dictionary<int, byte[]>());
        Console.WriteLine($"no-op identical: {same.AsSpan().SequenceEqual(bytes)}");
        var archive = DdpArchive.Open(bytes);
        var selfReplace = new Dictionary<int, byte[]> { [5] = bytes.AsSpan((int)archive.Index.Textures[5].Offset, (int)archive.Index.Textures[5].Length).ToArray() };
        Console.WriteLine($"self-replace identical: {DdpArchiveWriter.Rebuild(bytes, null, selfReplace).AsSpan().SequenceEqual(bytes)}");

        // Encoder quality on the first 60 BC3 textures.
        double oldSum = 0, newSum = 0; int count = 0; var sw = System.Diagnostics.Stopwatch.StartNew(); long pixels = 0;
        foreach (var t in archive.Index.Textures)
        {
            var nutBytes = bytes.AsMemory((int)t.Offset, (int)t.Length);
            var x = NutFile.Parse(nutBytes).Textures[0];
            if (x.Format != NutPixelFormat.Bc3 || x.Width < 64) continue;
            var rgba = NutTextureDecoder.DecodeRgba8(x);
            var oldNut = NutPixelEditor.ReplacePixels(nutBytes, 0, x.Width, x.Height, rgba);
            var newNut = NutTextureWriter.Write(nutBytes, x.Width, x.Height, rgba);
            var oldBack = NutTextureDecoder.DecodeRgba8(NutFile.Parse(oldNut).Textures[0]);
            var newBack = NutTextureDecoder.DecodeRgba8(NutFile.Parse(newNut).Textures[0]);
            oldSum += psnr(rgba, oldBack); newSum += psnr(rgba, newBack); count++; pixels += x.Width * x.Height;
            if (newNut.Length != nutBytes.Length) Console.WriteLine($"length differs {t.Index}: {newNut.Length} vs {nutBytes.Length}");
            if (count == 60) break;
        }
        Console.WriteLine($"BC3 re-encode PSNR over {count} textures: old {oldSum / count:0.00} dB, new {newSum / count:0.00} dB ({pixels / 1e6:0.0} Mpx in {sw.ElapsedMilliseconds} ms incl. both)");

        // Resize texture 5 to 2x, update F007 and rebuild.
        var entry = archive.Index.Textures[5];
        var nut = bytes.AsMemory((int)entry.Offset, (int)entry.Length);
        var tex = NutFile.Parse(nut).Textures[0];
        var src = NutTextureDecoder.DecodeRgba8(tex);
        int w = tex.Width * 2, h = tex.Height * 2; var big = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var xx = 0; xx < w; xx++) Array.Copy(src, ((y / 2) * tex.Width + xx / 2) * 4, big, (y * w + xx) * 4, 4);
        var resized = NutTextureWriter.Write(nut, w, h, big);
        var movieEntry = archive.Index.Movies.First(m => m.TextureBegin <= 5 && 5 < m.TextureEnd);
        var doc = new LmbEditDocument(archive.OpenMovie(movieEntry).Data.Span);
        Console.WriteLine($"F007 before {doc.TextureSize(5 - movieEntry.TextureBegin)}");
        doc.SetTextureSize(5 - movieEntry.TextureBegin, w, h);
        Console.WriteLine($"F007 after {doc.TextureSize(5 - movieEntry.TextureBegin)} layoutPreserved={doc.PreservesRecordLayout}");
        var rebuilt = DdpArchiveWriter.Rebuild(bytes, new Dictionary<string, byte[]> { [movieEntry.Name] = doc.Export(true) }, new Dictionary<int, byte[]> { [5] = resized });
        var after = DdpArchive.Open(rebuilt);
        var check = NutFile.Parse(after.OpenMovie(movieEntry.Name).Textures[5 - movieEntry.TextureBegin].Data).Textures[0];
        Console.WriteLine($"rebuilt: {bytes.Length} -> {rebuilt.Length} bytes, texture 5 now {check.Width}x{check.Height}, maxEntry {archive.Index.TrailerUnknown} -> {after.Index.TrailerUnknown}, movie len same: {after.Index.Movies[movieEntry.Index].Length == movieEntry.Length}");
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "lumenstudio-resize-test.ddp"), rebuilt);
    }
}
