using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;
static class PackTest
{
    public static void Run(string root)
    {
        int same = 0, diff = 0, err = 0; var notes = new List<string>();
        foreach (var f in Directory.GetFiles(root, "*.nut", SearchOption.AllDirectories))
        {
            var b = File.ReadAllBytes(f);
            try
            {
                var n = NutFile.Parse(b);
                var i = n.Textures.Length - 1;
                var single = NutPack.Extract(b, i);
                var back = NutPack.Rebuild(b, new Dictionary<int, byte[]> { [i] = single });
                if (back.AsSpan().SequenceEqual(b)) same++; else { diff++; if (notes.Count < 5) notes.Add($"{Path.GetRelativePath(root, f)} v{n.Version} n{n.Textures.Length} {b.Length} vs {back.Length}"); }
            }
            catch (Exception e) { err++; if (notes.Count < 8) notes.Add("ERR " + Path.GetRelativePath(root, f) + ": " + e.Message); }
        }
        Console.WriteLine($"NUT self-splice identical={same} different={diff} errors={err}");
        notes.ForEach(Console.WriteLine);
        int ns = 0, nd = 0;
        foreach (var f in Directory.GetFiles(root, "*.ndp", SearchOption.AllDirectories))
        {
            var b = File.ReadAllBytes(f);
            var a = NdpArchive.Parse(b);
            var back = NdpArchive.Rebuild(b, new Dictionary<int, byte[]> { [0] = a.Read(b, 0).ToArray() });
            if (back.AsSpan().SequenceEqual(b)) ns++; else { nd++; Console.WriteLine($"NDP diff {f}"); }
        }
        Console.WriteLine($"NDP self-rebuild identical={ns} different={nd}");
    }
}
