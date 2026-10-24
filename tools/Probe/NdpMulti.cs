using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;
static class NdpMulti
{
    public static void Run(string root)
    {
        int single = 0, multi = 0;
        foreach (var f in Directory.GetFiles(root, "*.ndp", SearchOption.AllDirectories))
        {
            var b = File.ReadAllBytes(f); var a = NdpArchive.Parse(b);
            foreach (var e in a.Entries) { var n = NutFile.Parse(a.Read(b, e.Index)); if (n.Textures.Length == 1 && n.Version == 1) single++; else multi++; }
        }
        Console.WriteLine($"NDP entries single v1={single} other={multi}");
    }
}
