using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;
static class Pad
{
    public static void Run(string root)
    {
        int shown = 0; var kinds = new Dictionary<string,int>();
        foreach (var file in Directory.GetFiles(root, "*.ddp", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var ix = DdpArchive.Open(bytes).Index;
            foreach (var t in ix.Textures)
            {
                var x = NutFile.Parse(bytes.AsMemory((int)t.Offset, (int)t.Length)).Textures[0];
                long raw = x.Format is NutPixelFormat.Bc1 ? ((x.Width + 3) / 4) * ((x.Height + 3) / 4) * 8L : x.Format is NutPixelFormat.Bc3 ? ((x.Width + 3) / 4) * ((x.Height + 3) / 4) * 16L : x.Width * x.Height * 4L;
                var k = $"dataSize-raw={x.DataSize - raw} total-hdr-data={x.TotalSize - x.HeaderSize - x.DataSize} entry-0x10-total={t.Length - 0x10 - x.TotalSize}";
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
                if (x.DataSize != raw && shown++ < 5) Console.WriteLine($"{x.Format} {x.Width}x{x.Height} raw={raw} data={x.DataSize} total={x.TotalSize} entry={t.Length} hdr={Convert.ToHexString(x.HeaderBytes.AsSpan())}");
            }
        }
        foreach (var k in kinds.OrderByDescending(p => p.Value)) Console.WriteLine($"{k.Value}: {k.Key}");
    }
}
