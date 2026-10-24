using Waddamburo.Formats.Ddp;
static class MovieCompare
{
    public static void Run(string a, string b)
    {
        var ab = File.ReadAllBytes(a); var bb = File.ReadAllBytes(b);
        var x = DdpArchive.Open(ab); var y = DdpArchive.Open(bb);
        foreach (var m in x.Index.Movies)
            Console.WriteLine($"{m.Name}: movie bytes identical = {x.OpenMovie(m).Data.Span.SequenceEqual(y.OpenMovie(m.Name).Data.Span)}");
        var changed = 0;
        for (var i = 0; i < x.Index.Textures.Length; i++)
            if (!ab.AsSpan((int)x.Index.Textures[i].Offset, (int)x.Index.Textures[i].Length).SequenceEqual(bb.AsSpan((int)y.Index.Textures[i].Offset, (int)y.Index.Textures[i].Length))) changed++;
        Console.WriteLine($"textures changed: {changed}/{x.Index.Textures.Length}");
    }
}
