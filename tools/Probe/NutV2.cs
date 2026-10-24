using Waddamburo.Formats.Nut;
static class NutV2
{
    public static void Run(string root)
    {
        var files = Directory.GetFiles(root, "*.nut", SearchOption.AllDirectories);
        var stats = new Dictionary<string, int>();
        foreach (var f in files)
        {
            var b = File.ReadAllBytes(f);
            NutFile n;
            try { n = NutFile.Parse(b); } catch (Exception e) { stats["parse error " + e.Message[..Math.Min(40, e.Message.Length)]] = stats.GetValueOrDefault("parse error") + 1; continue; }
            var key = $"v{n.Version} count{(n.Textures.Length == 1 ? "1" : ">1")} mips{(n.Textures.Any(t => t.MipCount > 1) ? ">1" : "1")} hdr{string.Join("/", n.Textures.Select(t => t.HeaderSize).Distinct())}";
            stats[key] = stats.GetValueOrDefault(key) + 1;
            if (n.Version == 2 && stats[key] <= 2)
            {
                Console.WriteLine($"{Path.GetRelativePath(root, f)} len=0x{b.Length:X} filehdr={Convert.ToHexString(n.HeaderBytes.AsSpan())}");
                foreach (var t in n.Textures.Take(4))
                    Console.WriteLine($"  hdrOff=0x{t.HeaderOffset:X} dataOff=0x{t.DataOffset:X} data=0x{t.DataSize:X} total=0x{t.TotalSize:X} {t.Width}x{t.Height} {t.Format} mips{t.MipCount} hdr={Convert.ToHexString(t.HeaderBytes.AsSpan(0, Math.Min(0x40, t.HeaderBytes.Length)))}");
                var last = n.Textures[^1];
                Console.WriteLine($"  last end=0x{last.DataOffset + last.DataSize:X}");
            }
        }
        foreach (var s in stats) Console.WriteLine($"{s.Value}: {s.Key}");
    }
}
