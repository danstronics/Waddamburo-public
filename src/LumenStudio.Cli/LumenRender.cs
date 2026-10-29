using System.Globalization;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Upscale;

/// <summary>
/// Headless playback of a movie for checking authored work: software-rendered PNGs, a scripted key
/// sequence, and a stand-in for Waddamburo's Wd API (a made-up catalog) so a Song Select runs alone.
/// </summary>
internal static class LumenRender
{
    public static int Run(string archivePath, string movieName, IReadOnlyList<string> args)
    {
        string? value(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..];
        var frames = int.Parse(value("frames") ?? "1", CultureInfo.InvariantCulture);
        var output = value("out") ?? Path.ChangeExtension(archivePath, ".png");
        var shots = (value("shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToHashSet();
        // --keys=frame:code[+frames],... e.g. 30:83 holds S on frame 30 (one frame); 60:90+3 holds Z three frames.
        var keys = new Dictionary<int, List<int>>();
        foreach (var spec in (value("keys") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (at, rest) = (spec.Split(':')[0], spec.Split(':')[1]);
            var parts = rest.Split('+');
            var hold = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1;
            for (var f = 0; f < hold; f++)
            {
                var frame = int.Parse(at, CultureInfo.InvariantCulture) + f;
                if (!keys.TryGetValue(frame, out var list)) keys[frame] = list = [];
                list.Add(char.IsDigit(parts[0][0]) ? int.Parse(parts[0], CultureInfo.InvariantCulture) : char.ToUpperInvariant(parts[0][0]));
            }
        }
        var (movie, textures) = LumenStudio.Authoring.Preview.PreviewMovie.Load(File.ReadAllBytes(archivePath), movieName);
        var host = new LumenStudio.Authoring.Preview.PreviewSongSelectHost();
        var player = LumenStudio.Authoring.Preview.PreviewMovie.Start(movie, host);
        void shot(string path)
        {
            var snapshot = player.CreateRenderSnapshot();
            var pixels = LumenSoftwareRenderer.Render(snapshot, textures, 1280, 720);
            TextOverlay.Draw(pixels, 1280, 720, snapshot);
            PngFile.Write(path, 1280, 720, pixels);
            Console.WriteLine($"frame {player.CurrentFrame}: {path}");
        }
        for (var frame = 1; frame <= frames; frame++)
        {
            player.Advance(new LumenInputSnapshot(keys.GetValueOrDefault(frame) ?? []));
            if (shots.Contains(frame)) shot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"{Path.GetFileNameWithoutExtension(output)}-{frame:D4}.png"));
        }
        shot(output);
        foreach (var line in host.Log) Console.WriteLine("host: " + line);
        foreach (var diagnostic in player.Diagnostics.DistinctBy(d => d.Code + d.Message).Take(20))
            Console.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        return 0;
    }
}

/// <summary>Stands in for the game's text rasterizer: one block per character of each dynamic text field, in its colour.</summary>
internal static class TextOverlay
{
    public static void Draw(byte[] pixels, int width, int height, LumenRenderSnapshot snapshot)
    {
        foreach (var quad in snapshot.Quads)
        {
            if (quad.NativeSurface is not { } surface || !LumenTextSurface.TryParse(surface, out var text, out _, out _, out var size, out _, out var rgb)) continue;
            var x0 = (int)Math.Min(quad.TopLeft.X, quad.BottomLeft.X); var y0 = (int)Math.Min(quad.TopLeft.Y, quad.TopRight.Y);
            var scaleX = 1280f / snapshot.StageWidth;
            var glyph = Math.Max(4, (int)(size * 0.6f * scaleX));
            var h = Math.Max(4, (int)(size * 0.8f * scaleX));
            var alpha = quad.MultiplyColor.Alpha;
            for (var c = 0; c < text.Length; c++)
            {
                if (char.IsWhiteSpace(text[c])) continue;
                for (var y = y0 + 2; y < y0 + 2 + h; y++)
                    for (var x = x0 + c * glyph + 1; x < x0 + (c + 1) * glyph - 1; x++)
                    {
                        if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
                        var i = (y * width + x) * 4;
                        pixels[i] = (byte)(pixels[i] * (1 - alpha) + ((rgb >> 16) & 0xFF) * alpha);
                        pixels[i + 1] = (byte)(pixels[i + 1] * (1 - alpha) + ((rgb >> 8) & 0xFF) * alpha);
                        pixels[i + 2] = (byte)(pixels[i + 2] * (1 - alpha) + (rgb & 0xFF) * alpha);
                    }
            }
        }
    }
}
