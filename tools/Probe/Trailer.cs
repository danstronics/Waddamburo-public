using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;
static class Trailer
{
    public static void Run(string root)
    {
        int eq = 0, ne = 0;
        foreach (var file in Directory.GetFiles(root, "*.ddp", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var ix = DdpArchive.Open(bytes).Index;
            long maxLen = ix.Textures.Length == 0 ? 0 : ix.Textures.Max(t => (long)t.Length);
            if (ix.TrailerUnknown == maxLen) { eq++; continue; }
            ne++;
            Console.WriteLine($"MISMATCH unk={ix.TrailerUnknown} maxTexEntry={maxLen} tex={ix.Textures.Length} trTex={ix.TrailerTextureCount} {Path.GetRelativePath(root, file)}");
        }
        Console.WriteLine($"equal={eq} notEqual={ne}");
    }
}
