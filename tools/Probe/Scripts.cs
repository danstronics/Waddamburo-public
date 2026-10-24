using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
static class Scripts
{
    public static void Run(string ddp, string movieName)
    {
        var a = DdpArchive.Open(File.ReadAllBytes(ddp));
        var m = LmbSemanticReader.Read(LmbFile.Parse(a.OpenMovie(movieName).Data)).Value;
        var root = m.Properties!.RootCharacterId!.Value;
        var sprite = m.Sprites.Single(s => s.CharacterId == root);
        int? frame = null;
        foreach (var c in sprite.Timeline)
        {
            if (c is LmbShowFrameCommand f) frame = (int)f.Frame;
            if (c is LmbFrameKeyCommand) frame = null;
            if (c is LmbDoActionCommand d && frame is int at)
                Console.WriteLine($"--- root frame {at}: action {d.ActionIndex}\n" + Avm1Assembly.Disassemble(m.Actions[(int)d.ActionIndex], m.Strings).Split('\n').Skip(2).Take(30).Aggregate("", (x, y) => x + y + "\n"));
        }
    }
}
