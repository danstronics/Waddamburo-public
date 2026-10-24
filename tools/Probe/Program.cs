using System.Buffers.Binary;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;

if (args[0] == "packtest") { PackTest.Run(args[1]); return; }
if (args[0] == "nutv2") { NutV2.Run(args[1]); return; }
if (args[0] == "ndp") { Ndp.Run(args[1]); return; }
if (args[0] == "moviecmp") { MovieCompare.Run(args[1], args[2]); return; }
if (args[0] == "writer") { WriterTest.Run(args[1]); return; }
if (args[0] == "pad") { Pad.Run(args[1]); return; }
if (args[0] == "records") { Records.Run(args[1]); return; }
if (args[0] == "trailer") { Trailer.Run(args[1]); return; }
if (args[0] == "survey") { Survey.Run(args[1]); return; }
var path = args[0];
var bytes = File.ReadAllBytes(path);
var archive = DdpArchive.Open(bytes);
var ix = archive.Index;
Console.WriteLine($"len={bytes.Length} movieOff=0x{ix.MovieDataOffset:X} movieLen=0x{ix.MovieDataLength:X} texOff=0x{ix.TextureDataOffset:X} texLen=0x{ix.TextureDataLength:X} trailerUnk={ix.TrailerUnknown} trailerTexCount={ix.TrailerTextureCount} texCount={ix.Textures.Length} trailing={ix.TrailingByteCount}");
Console.WriteLine("header hex: " + Convert.ToHexString(bytes, 0, 64));
foreach (var m in ix.Movies) Console.WriteLine($"movie {m.Index} {m.Name} rel=0x{m.RelativeOffset:X} len=0x{m.Length:X} tex[{m.TextureBegin},{m.TextureEnd})");
long gcd(long a, long b) => b == 0 ? a : gcd(b, a % b);
long g = 0; long prevEnd = 0; long gaps = 0;
foreach (var t in ix.Textures) { g = gcd(g, t.RelativeOffset); if (t.RelativeOffset != prevEnd) gaps++; prevEnd = t.RelativeOffset + t.Length; }
Console.WriteLine($"texture rel offset gcd=0x{g:X}, nonContiguous={gaps}, lastEnd=0x{prevEnd:X} vs texLen=0x{ix.TextureDataLength:X}");
var formats = new Dictionary<string, int>();
foreach (var t in ix.Textures.Take(int.Parse(args.ElementAtOrDefault(1) ?? "100000")))
{
    var data = bytes.AsMemory((int)t.Offset, (int)t.Length);
    var nut = NutFile.Parse(data);
    var x = nut.Textures[0];
    var key = $"v{nut.Version} fmt{x.Format} mip{x.MipCount} hdr{x.HeaderSize}";
    formats[key] = formats.GetValueOrDefault(key) + 1;
    if (t.Index < 4 || (x.MipCount > 1 && formats[key] < 3))
    {
        Console.WriteLine($"tex {t.Index} rel=0x{t.RelativeOffset:X} len=0x{t.Length:X} {x.Width}x{x.Height} {key} total=0x{x.TotalSize:X} data=0x{x.DataSize:X} dataOff=0x{x.DataOffset:X} gid={x.GlobalId}");
        Console.WriteLine("  file hdr " + Convert.ToHexString(nut.HeaderBytes.AsSpan()));
        Console.WriteLine("  tex hdr  " + Convert.ToHexString(x.HeaderBytes.AsSpan()));
        var tail = (int)(x.DataOffset + x.DataSize);
        if (tail < data.Length) Console.WriteLine($"  after payload {data.Length - tail} bytes: " + Convert.ToHexString(data.Span[tail..Math.Min(data.Length, tail + 48)]));
    }
}
foreach (var f in formats) Console.WriteLine($"{f.Key}: {f.Value}");

foreach (var m in ix.Movies.Take(2))
{
    var view = archive.OpenMovie(m);
    var movie = LmbSemanticReader.Read(LmbFile.Parse(view.Data)).Value;
    Console.WriteLine($"== {m.Name}: shapes={movie.Shapes.Length} sprites={movie.Sprites.Length} texts={movie.Texts.Length} actions={movie.Actions.Length} strings={movie.Strings.Length} texBounds={movie.TextureBounds.Length}");
    var tags = movie.RawFile.Records.GroupBy(r => r.Tag).Select(gr => $"{gr.Key:X4}x{gr.Count()}");
    Console.WriteLine("tags " + string.Join(" ", tags));
    foreach (var s in movie.Shapes.Take(6))
        foreach (var geo in s.Geometry.Take(2))
        {
            var tex = geo.TextureIndex < view.Textures.Length ? NutFile.Parse(view.Textures[(int)geo.TextureIndex].Data).Textures[0] : null;
            Console.WriteLine($"shape {s.CharacterId} hdr[{string.Join(",", s.UninterpretedHeaderWords)}] tex={geo.TextureIndex} ({tex?.Width}x{tex?.Height}) flags=0x{geo.Flags:X} unk[{string.Join(",", geo.UninterpretedWords)}] verts " +
                string.Join(" ", geo.Vertices.Select(v => $"({v.X:0.#},{v.Y:0.#} uv {v.U:0.####},{v.V:0.####})")));
        }
    foreach (var tb in movie.TextureBounds.Take(4)) Console.WriteLine($"texbounds {tb}");
    foreach (var b in movie.Bounds.Take(4)) Console.WriteLine($"bounds {b}");
    foreach (var t in movie.Texts.Take(3)) Console.WriteLine($"text {t.CharacterId} html='{movie.Strings[(int)t.HtmlStringIndex].Value}' words[{string.Join(",", t.Words)}]");
    var rec = movie.RawFile.Records.FirstOrDefault(r => r.Tag == 0xF004);
}
