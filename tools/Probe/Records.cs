using System.Buffers.Binary;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
static class Records
{
    public static void Run(string ddp)
    {
        var bytes = File.ReadAllBytes(ddp);
        var a = DdpArchive.Open(bytes);
        var m = a.OpenMovie(a.Index.Movies[0]);
        var file = LmbFile.Parse(m.Data);
        foreach (var r in file.Records.Where(r => r.Tag is 0xF008 or 0xF009 or 0xF00A or 0xF00B or 0xF00D or 0x000A or 0xFF00 or 0xF00C or 0xF007))
        {
            var p = r.Payload.ToArray();
            var words = Enumerable.Range(0, Math.Min(24, p.Length / 4)).Select(i => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i * 4, 4)));
            Console.WriteLine($"{r.Tag:X4} words={r.WordCount}: " + string.Join(" ", words.Select(w => w.ToString("X8"))));
        }
        Console.WriteLine("header " + Convert.ToHexString(file.HeaderBytes.AsSpan()));
    }
}
