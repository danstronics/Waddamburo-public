using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;

static class Survey
{
    public static void Run(string root)
    {
        var dims = new Dictionary<string, int>();
        int total = 0, npot = 0, argbNotMult16 = 0, bcNotMult4 = 0, multiMip = 0, v2 = 0, multiTex = 0;
        var hdrSizes = new Dictionary<int, int>(); int maxDim = 0; long gcdAll = 0; var gidNonZero = 0; var extraTail = 0;
        long gcd(long a, long b) => b == 0 ? a : gcd(b, a % b);
        var ddps = Directory.GetFiles(root, "*.ddp", SearchOption.AllDirectories);
        var trailerMismatch = 0; var movieGcd = 0L; var nonContig = 0;
        foreach (var file in ddps)
        {
            var bytes = File.ReadAllBytes(file);
            var a = DdpArchive.Open(bytes);
            if (a.Index.TrailerTextureCount != a.Index.Textures.Length) trailerMismatch++;
            long prev = 0;
            foreach (var m in a.Index.Movies) movieGcd = gcd(movieGcd, m.RelativeOffset);
            foreach (var t in a.Index.Textures)
            {
                if (t.RelativeOffset != prev) nonContig++;
                prev = t.RelativeOffset + t.Length;
                gcdAll = gcd(gcdAll, t.RelativeOffset);
                var nut = NutFile.Parse(bytes.AsMemory((int)t.Offset, (int)t.Length));
                if (nut.Version == 2) v2++;
                if (nut.Textures.Length != 1) multiTex++;
                foreach (var x in nut.Textures)
                {
                    total++;
                    hdrSizes[x.HeaderSize] = hdrSizes.GetValueOrDefault(x.HeaderSize) + 1;
                    bool pot(int v) => (v & (v - 1)) == 0;
                    if (!pot(x.Width) || !pot(x.Height)) npot++;
                    if (x.Format is NutPixelFormat.Argb or NutPixelFormat.ArgbAlternate && x.Width % 16 != 0) argbNotMult16++;
                    if (x.Format is NutPixelFormat.Bc1 or NutPixelFormat.Bc3 && (x.Width % 4 != 0 || x.Height % 4 != 0)) bcNotMult4++;
                    if (x.MipCount > 1) multiMip++;
                    if (x.GlobalId is > 0) gidNonZero++;
                    maxDim = Math.Max(maxDim, Math.Max(x.Width, x.Height));
                    if (x.DataOffset + x.DataSize != t.Length) extraTail++;
                    var k = $"{x.Format}";
                    dims[k] = dims.GetValueOrDefault(k) + 1;
                }
            }
            if (prev != a.Index.TextureDataLength) Console.WriteLine($"tex block tail gap in {file}: {a.Index.TextureDataLength - prev}");
        }
        Console.WriteLine($"ddps={ddps.Length} textures={total} npot={npot} argbWidthNot16={argbNotMult16} bcNot4={bcNotMult4} multiMip={multiMip} v2={v2} multiTexNut={multiTex} maxDim={maxDim} texOffGcd=0x{gcdAll:X} movieOffGcd=0x{movieGcd:X} gidNonZero={gidNonZero} payloadNotAtEnd={extraTail} trailerMismatch={trailerMismatch} nonContig={nonContig}");
        Console.WriteLine("formats " + string.Join(", ", dims.Select(p => $"{p.Key}={p.Value}")));
        Console.WriteLine("headers " + string.Join(", ", hdrSizes.Select(p => $"0x{p.Key:X}={p.Value}")));
    }
}
