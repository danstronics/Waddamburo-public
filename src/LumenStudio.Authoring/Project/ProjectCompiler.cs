using System.Globalization;
using LumenStudio.Authoring.Lmb;
using LumenStudio.Authoring.Packaging;
using LumenStudio.Authoring.Script;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;
using Waddamburo.Upscale;

namespace LumenStudio.Authoring.Project;

public sealed record CompiledMovie(LumenProject Project, byte[] Archive, byte[] Lmb, int Textures, IReadOnlyList<string> Log);

/// <summary>Builds a project into a Green archive: LMB from scratch, NUT textures, one DDP.</summary>
public static class ProjectCompiler
{
    /// <summary>Marker string of every LumenStudio-authored movie (Waddamburo's AuthoredMovie.Marker).</summary>
    public const string AuthoredMarker = "wd:authored/1";

    public static CompiledMovie Compile(string projectPath, string? gameRoot)
    {
        var full = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(full)!;
        var project = LumenProject.Load(full);
        if (string.IsNullOrWhiteSpace(project.Archive) || string.IsNullOrWhiteSpace(project.Movie))
            throw new InvalidDataException("The project names no archive and movie.");
        var log = new List<string>();
        string resolve(string path)
        {
            if (path.Contains("${game}", StringComparison.Ordinal))
                path = path.Replace("${game}", gameRoot ?? throw new InvalidDataException($"'{path}' needs the game folder (--game)."), StringComparison.Ordinal);
            return Path.GetFullPath(Path.Combine(directory, path));
        }

        var builder = new LmbBuilder { Width = project.Width, Height = project.Height, FrameRate = project.Fps };
        builder.String(AuthoredMarker);
        var textureData = new Dictionary<int, byte[]>();
        var sources = new Dictionary<string, (MovieImporter Importer, DdpMovieView View)>(StringComparer.Ordinal);
        foreach (var import in project.Imports)
        {
            var bytes = File.ReadAllBytes(resolve(import.Archive));
            var view = DdpArchive.Open(bytes).OpenMovie(import.Movie);
            var definition = LmbSemanticReader.Read(LmbFile.Parse(view.Data)).Value;
            var sizes = view.Textures.Select(t => NutFile.Parse(t.Data).Textures[0]).Select(t => ((int)t.Width, (int)t.Height)).ToList();
            sources.Add(import.Id, (new MovieImporter(builder, definition, sizes) { StripScripts = import.StripScripts }, view));
            log.Add($"import {import.Id}: {import.Movie} ({definition.Sprites.Length} sprites, {sizes.Count} textures)");
        }

        var images = new Dictionary<string, (int Index, int Width, int Height)>(StringComparer.Ordinal);
        foreach (var texture in project.Textures)
        {
            var (width, height, rgba) = PngFile.Read(resolve(texture.File));
            var format = (texture.Format ?? "bc3").ToLowerInvariant() switch
            {
                "bc1" => NutPixelFormat.Bc1,
                "bc2" => NutPixelFormat.Bc2,
                "bc3" => NutPixelFormat.Bc3,
                "argb" => NutPixelFormat.Argb,
                var other => throw new InvalidDataException($"Texture {texture.Id}: unknown format '{other}'."),
            };
            var index = builder.Texture((int)width, (int)height);
            textureData[index] = NutBuilder.FromRgba((int)width, (int)height, rgba, format);
            images.Add(texture.Id, (index, (int)width, (int)height));
        }

        var solids = new Dictionary<uint, int>();
        int solid(uint argb)
        {
            if (solids.TryGetValue(argb, out var index)) return index;
            var pixels = new byte[8 * 8 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
            index = builder.Texture(8, 8);
            textureData[index] = NutBuilder.FromRgba(8, 8, pixels);
            solids.Add(argb, index);
            return index;
        }

        var symbols = project.Symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var characters = new Dictionary<string, uint>(StringComparer.Ordinal);
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        uint character(string id)
        {
            if (characters.TryGetValue(id, out var existing)) return existing;
            if (!symbols.TryGetValue(id, out var symbol))
            {
                // "importId:Name" works without a symbol entry too.
                if (id.Split(':', 2) is [var source, var name] && sources.TryGetValue(source, out var shortcut))
                    return characters[id] = shortcut.Importer.Import(name);
                throw new InvalidDataException($"Unknown symbol '{id}'.");
            }
            if (!resolving.Add(id)) throw new InvalidDataException($"Symbol '{id}' contains itself.");
            var made = symbol.Kind switch
            {
                "import" => importSymbol(symbol),
                "image" => imageSymbol(symbol),
                "rect" => rectSymbol(symbol),
                "text" => textSymbol(symbol),
                "sprite" => spriteSymbol(symbol),
                var other => throw new InvalidDataException($"Symbol '{id}': unknown kind '{other}'."),
            };
            resolving.Remove(id);
            return characters[id] = made;
        }

        uint importSymbol(SymbolSource symbol)
        {
            if (symbol.From?.Split(':', 2) is not [var source, var name] || !sources.TryGetValue(source, out var import))
                throw new InvalidDataException($"Symbol '{symbol.Id}': 'from' must be importId:Name.");
            return import.Importer.Import(name);
        }

        uint quad(SymbolSource symbol, int texture, float width, float height, (float U0, float V0, float U1, float V1) uv)
        {
            var left = -symbol.OriginX * width;
            var top = -symbol.OriginY * height;
            var shape = builder.Shape(builder.Bounds(left, top, left + width, top + height));
            shape.Quads.Add(new ShapeQuad(
            [
                new(left, top, uv.U0, uv.V0), new(left + width, top, uv.U1, uv.V0),
                new(left + width, top + height, uv.U1, uv.V1), new(left, top + height, uv.U0, uv.V1),
            ], (uint)texture, ShapeQuad.Textured));
            return shape.Id;
        }

        uint imageSymbol(SymbolSource symbol)
        {
            if (symbol.Texture is null || !images.TryGetValue(symbol.Texture, out var image))
                throw new InvalidDataException($"Symbol '{symbol.Id}': unknown texture '{symbol.Texture}'.");
            var region = symbol.Region ?? [0, 0, image.Width, image.Height];
            if (region.Length != 4) throw new InvalidDataException($"Symbol '{symbol.Id}': region is [x, y, width, height].");
            return quad(symbol, image.Index, symbol.Width ?? region[2], symbol.Height ?? region[3],
                (region[0] / image.Width, region[1] / image.Height, (region[0] + region[2]) / image.Width, (region[1] + region[3]) / image.Height));
        }

        uint rectSymbol(SymbolSource symbol) =>
            quad(symbol, solid(parseColor(symbol.Color ?? "#FFFFFFFF", symbol.Id)), symbol.Width ?? 100, symbol.Height ?? 100, (0.25f, 0.25f, 0.75f, 0.75f));

        uint textSymbol(SymbolSource symbol)
        {
            var width = symbol.Width ?? 400;
            var height = symbol.Height ?? 40;
            var rgb = parseColor(symbol.Color ?? "#FFFFFF", symbol.Id) & 0xFFFFFF;
            return builder.Text(-symbol.OriginX * width, -symbol.OriginY * height, width, height, symbol.Size ?? 24,
                symbol.Align ?? "left", "#" + rgb.ToString("X6", CultureInfo.InvariantCulture)).Id;
        }

        uint spriteSymbol(SymbolSource symbol)
        {
            var layers = symbol.Layers ?? [];
            var children = layers.Select(layer => character(layer.Symbol)).ToArray();
            var sprite = builder.Sprite();
            sprite.ExportName = symbol.Export;
            var scripts = (symbol.Scripts ?? []).Select(pair => (Frame: int.Parse(pair.Key, CultureInfo.InvariantCulture), Code: pair.Value)).ToList();
            var frames = new[]
            {
                symbol.Frames ?? 1,
                (symbol.Labels?.Values.DefaultIfEmpty(0).Max() ?? 0) + 1,
                scripts.Select(s => s.Frame + 1).DefaultIfEmpty(1).Max(),
                layers.SelectMany(l => l.Keys.Select(k => k.Frame + 1).Append((l.To ?? 0) + 1)).DefaultIfEmpty(1).Max(),
            }.Max();
            foreach (var (name, frame) in symbol.Labels ?? []) sprite.Labels.Add((name, frame));
            for (var i = 0; i < layers.Count; i++)
                placeLayer(sprite, layers[i], children[i], (ushort)(i + 1), frames, symbol.Id);
            foreach (var (frame, code) in scripts.OrderBy(s => s.Frame))
            {
                var source = code.StartsWith('@') ? File.ReadAllText(resolve(code[1..])) : code;
                byte[] bytecode;
                try { bytecode = ScriptCompiler.Compile(source, builder.String); }
                catch (ScriptException error)
                {
                    throw new InvalidDataException($"{(code.StartsWith('@') ? code[1..] : $"{symbol.Id} frame {frame}")}:{error.Message}", error);
                }
                sprite.Frame(frame).Add(new ActionCommand((uint)builder.Action(bytecode)));
            }
            sprite.Frame(frames - 1);
            return sprite.Id;
        }

        void placeLayer(SpriteDefinition sprite, LayerSource layer, uint child, ushort depth, int frames, string owner)
        {
            var keys = layer.Keys.OrderBy(k => k.Frame).ToList();
            if (keys.Count == 0) keys.Add(new KeySource { Frame = layer.From ?? 0 });
            var states = resolveKeys(keys);
            var start = layer.From ?? keys[0].Frame;
            var end = Math.Min(layer.To ?? frames - 1, frames - 1);
            var name = layer.Name is { Length: > 0 } instance ? (uint)builder.String(instance) : 0;
            var blend = (layer.Blend ?? "normal").ToLowerInvariant() switch
            {
                "normal" => (ushort)0,
                "multiply" => (ushort)3,
                "screen" => (ushort)4,
                "add" => (ushort)8,
                var other => throw new InvalidDataException($"{owner}: unknown blend '{other}'."),
            };
            (int Matrix, int Color)? previous = null;
            for (var frame = start; frame <= end; frame++)
            {
                var state = sample(states, frame);
                var matrix = builder.Matrix(Affine.Compose(state.X, state.Y, state.ScaleX, state.ScaleY, state.Rotation));
                var color = builder.Color(new ColorEntry(channel(state.R), channel(state.G), channel(state.B), channel(state.Alpha)));
                if (previous == (matrix, color)) continue;
                sprite.Frame(frame).Add(new PlaceCommand(child, depth, previous is null ? name : 0, previous is null ? (ushort)1 : (ushort)2,
                    blend, depth, 0, (ushort)frame, PlaceCommand.MatrixPosition, checked((ushort)matrix), (uint)color, PlaceCommand.NoColor));
                previous = (matrix, color);
            }
            if (end + 1 < frames) sprite.Frame(end + 1).Add(new RemoveCommand(child, depth));
        }

        // Every declared symbol is built, not only those placed: scripts attach exports by name.
        foreach (var symbol in project.Symbols) character(symbol.Id);
        builder.RootId = character(project.Root);
        var lmb = builder.Build();
        var textures = new byte[builder.Textures.Count][];
        foreach (var (index, data) in textureData) textures[index] = data;
        foreach (var (_, (importer, view)) in sources)
            foreach (var (source, target) in importer.TextureMap) textures[target] = view.Textures[source].Data.ToArray();
        var parsed = LmbSemanticReader.Read(LmbFile.Parse(lmb), validationContext: new LmbSemanticValidationContext(textures.Length));
        var errors = parsed.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new InvalidDataException("The built movie does not validate: " + string.Join("; ", errors.Select(e => e.Message)));
        var archive = ArchiveWriter.Write([new ArchiveMovie(project.Movie, lmb, textures)]);
        log.Add($"{project.Movie}: {lmb.Length:N0} bytes LMB, {parsed.Value.Sprites.Length} sprites, {parsed.Value.Shapes.Length} shapes, "
            + $"{parsed.Value.Actions.Length} scripts, {textures.Length} textures; archive {archive.Length:N0} bytes");
        return new CompiledMovie(project, archive, lmb, textures.Length, log);
    }

    private sealed record State(float X, float Y, float ScaleX, float ScaleY, float Rotation, float Alpha, float R, float G, float B, string Tween, int Frame);

    private static List<State> resolveKeys(List<KeySource> keys)
    {
        var states = new List<State>();
        var current = new State(0, 0, 1, 1, 0, 1, 1, 1, 1, "linear", 0);
        foreach (var key in keys)
        {
            var (r, g, b) = key.Tint is { } tint ? rgb(parseColor(tint, "tint")) : (current.R, current.G, current.B);
            current = new State(key.X ?? current.X, key.Y ?? current.Y, key.ScaleX ?? current.ScaleX, key.ScaleY ?? current.ScaleY,
                key.Rotation ?? current.Rotation, key.Alpha ?? current.Alpha, r, g, b, key.Tween ?? "linear", key.Frame);
            states.Add(current);
        }
        return states;
    }

    private static State sample(List<State> states, int frame)
    {
        var index = states.FindLastIndex(s => s.Frame <= frame);
        if (index < 0) return states[0];
        var a = states[index];
        if (index + 1 >= states.Count || a.Tween == "hold") return a;
        var b = states[index + 1];
        var t = (float)(frame - a.Frame) / (b.Frame - a.Frame);
        t = a.Tween switch
        {
            "easeIn" => t * t,
            "easeOut" => 1 - (1 - t) * (1 - t),
            "easeInOut" => t < 0.5f ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t),
            "linear" => t,
            var other => throw new InvalidDataException($"Unknown tween '{other}'."),
        };
        float lerp(float x, float y) => x + (y - x) * t;
        return new State(lerp(a.X, b.X), lerp(a.Y, b.Y), lerp(a.ScaleX, b.ScaleX), lerp(a.ScaleY, b.ScaleY), lerp(a.Rotation, b.Rotation),
            lerp(a.Alpha, b.Alpha), lerp(a.R, b.R), lerp(a.G, b.G), lerp(a.B, b.B), a.Tween, frame);
    }

    private static short channel(float value) => (short)Math.Clamp(MathF.Round(value * 256), 0, 256);

    private static (float R, float G, float B) rgb(uint argb) => (((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f);

    private static uint parseColor(string text, string owner)
    {
        var hex = text.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        return hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value
            : throw new InvalidDataException($"{owner}: '{text}' is not a #RRGGBB or #AARRGGBB colour.");
    }
}
