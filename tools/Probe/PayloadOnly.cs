using Waddamburo.Formats.Ddp;
static class PayloadOnly
{
    // Proves an edited DDP differs from its previous version only inside texture pixel payloads.
    public static void Run(string before, string after)
    {
        var a = File.ReadAllBytes(before); var b = File.ReadAllBytes(after);
        if (a.Length != b.Length) { Console.WriteLine($"DIFFERENT LENGTH {a.Length} vs {b.Length}: {after}"); return; }
        var index = DdpArchive.Open(a).Index;
        var payload = new bool[a.Length];
        foreach (var t in index.Textures) for (var i = t.Offset + 0x60; i < t.Offset + t.Length; i++) payload[i] = true;
        long changed = 0, outside = 0;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) { changed++; if (!payload[i]) outside++; }
        Console.WriteLine($"{(outside == 0 ? "PIXELS ONLY" : "STRUCTURE CHANGED")}: {changed:N0} bytes differ, {outside} outside texture payloads · {after}");
    }
}
