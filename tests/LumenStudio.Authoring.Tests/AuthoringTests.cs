using LumenStudio.Authoring.Lmb;
using LumenStudio.Authoring.Packaging;
using LumenStudio.Authoring.Script;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Runtime;

namespace LumenStudio.Authoring.Tests;

public sealed class AuthoringTests
{
    private static LmbMovieDefinition parse(byte[] lmb, int textures = 0)
    {
        var result = LmbSemanticReader.Read(LmbFile.Parse(lmb), validationContext: new LmbSemanticValidationContext(textures));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return result.Value;
    }

    /// <summary>A root sprite whose frame 0 runs <paramref name="script"/>; <paramref name="setup"/> adds characters.</summary>
    private static LumenPlayer run(string script, Action<LmbBuilder, SpriteDefinition>? setup = null, ILumenHostBinding? host = null, int frames = 1)
    {
        var builder = new LmbBuilder();
        var root = builder.Sprite();
        setup?.Invoke(builder, root);
        root.Frame(0).Add(new ActionCommand((uint)builder.Action(ScriptCompiler.Compile(script, builder.String))));
        root.Frame(frames - 1);
        builder.RootId = root.Id;
        return new LumenPlayer(parse(builder.Build(), builder.Textures.Count), 1280, 720, hostBinding: host);
    }

    private static double number(LumenPlayer player, string path) => Assert.IsType<double>(player.ReadScriptValue(path));

    [Fact]
    public void Language_core_runs_on_waddamburos_interpreter()
    {
        var player = run("""
            var a = 2 + 3 * 4;            // 14
            var s = "n=" + a;
            function fact(n) { if (n <= 1) { return 1; } return n * fact(n - 1); }
            var f = fact(5);              // 120
            var total = 0;
            for (var i = 0; i < 10; i++) { if (i == 3) continue; if (i == 8) break; total += i; }   // 0+1+2+4+5+6+7 = 25
            var w = 0; while (w < 7) w++;
            var d = 0; do { d += 2; } while (d < 5);   // 6
            var list = [10, 20, 30]; list.push(40);
            var len = list.length; var third = list[2];
            var o = { x: 5, y: "z" }; o.x += 10; o.twice = function (v) { return v * 2; };
            var tw = o.twice(o.x);        // 30
            var t = (a > 10 && f == 120) ? "yes" : "no";
            var orr = 0 || "fallback";
            var sw = "";
            switch (len) { case 3: sw = "three"; break; case 4: sw = "four"; case 5: sw += "+"; break; default: sw = "other"; }
            var fl = Math.floor(7.8) + Math.max(2, 9);   // 16
            var neg = -a; var bits = (5 & 3) | (1 << 4); var mod = 17 % 5;
            var notv = !false;
            """);
        Assert.Equal(14, number(player, "a"));
        Assert.Equal("n=14", player.ReadScriptValue("s"));
        Assert.Equal(120, number(player, "f"));
        Assert.Equal(25, number(player, "total"));
        Assert.Equal(7, number(player, "w"));
        Assert.Equal(6, number(player, "d"));
        Assert.Equal(4, number(player, "len"));
        Assert.Equal(30, number(player, "third"));
        Assert.Equal(15, number(player, "o.x"));
        Assert.Equal(30, number(player, "tw"));
        Assert.Equal("yes", player.ReadScriptValue("t"));
        Assert.Equal("fallback", player.ReadScriptValue("orr"));
        Assert.Equal("four+", player.ReadScriptValue("sw"));
        Assert.Equal(16, number(player, "fl"));
        Assert.Equal(-14, number(player, "neg"));
        Assert.Equal(17, number(player, "bits"));
        Assert.Equal(2, number(player, "mod"));
        Assert.Equal(true, player.ReadScriptValue("notv"));
        Assert.DoesNotContain(player.Diagnostics, d => d.Code.StartsWith("LUM_AVM", StringComparison.Ordinal));
    }

    [Fact]
    public void Timeline_labels_and_frame_scripts_drive_playback()
    {
        var builder = new LmbBuilder();
        var root = builder.Sprite();
        root.Labels.Add(("loop", 5));
        root.Frame(0).Add(new ActionCommand((uint)builder.Action(ScriptCompiler.Compile("var hits = 0; gotoAndPlay(\"loop\");", builder.String))));
        root.Frame(7).Add(new ActionCommand((uint)builder.Action(ScriptCompiler.Compile("hits++; stop();", builder.String))));
        root.Frame(9);
        builder.RootId = root.Id;
        var player = new LumenPlayer(parse(builder.Build()), 1280, 720);
        for (var i = 0; i < 6; i++) player.Advance();
        Assert.Equal(1, number(player, "hits"));
        Assert.Equal((7, false), player.InstanceFrame(""));
    }

    [Fact]
    public void Attached_exports_and_text_fields_are_scriptable()
    {
        var player = run("""
            var row = this.attachMovie("Row", "row0", 10);
            row._x = 200; row._y = 50;
            row.label.text = "Hello";
            var got = row.label.text;
            """, (builder, _) =>
        {
            var text = builder.Text(0, 0, 300, 40, 24, "left", "#FFFFFF");
            var row = builder.Sprite();
            row.ExportName = "Row";
            row.Frame(0).Add(new PlaceCommand(text.Id, 1, (uint)builder.String("label"), 1, 0, 1, 0, 0,
                PlaceCommand.MatrixPosition, (ushort)builder.Matrix(Affine.Identity), PlaceCommand.NoColor, PlaceCommand.NoColor));
        });
        Assert.Equal("Hello", player.ReadScriptValue("got"));
        Assert.True(player.TryGetInstanceLocalTransform("row0", out var transform));
        Assert.Equal(200, transform.X);
        var snapshot = player.CreateRenderSnapshot();
        Assert.Contains(snapshot.Quads, q => q.NativeSurface is not null);
    }

    private sealed class EchoHost : ILumenHostBinding
    {
        public List<string> Calls { get; } = [];

        public void Install(LumenHostContext context) => context.RegisterObject("Wd", api =>
        {
            api.RegisterMethod("songTitle", call => LumenHostValue.FromString($"Song {call.Arguments[0].AsNumber()}-{call.Arguments[1].AsNumber()}"));
            api.RegisterMethod("select", call => { Calls.Add($"select {call.Arguments[0].AsNumber()} {call.Arguments[1].AsNumber()}"); return LumenHostValue.FromBoolean(true); });
        });
    }

    [Fact]
    public void Host_calls_cross_both_ways()
    {
        var host = new EchoHost();
        var player = run("""
            import flash.external.ExternalInterface;
            var title = Wd.songTitle(2, 7);
            var started = -1;
            ExternalInterface.addCallback("wdStart", this, function (genre, song) { started = genre * 100 + song; Wd.select(genre, song); });
            """, host: host);
        Assert.Equal("Song 2-7", player.ReadScriptValue("title"));
        Assert.True(player.TryInvokeCallback("wdStart", [LumenHostValue.FromNumber(3), LumenHostValue.FromNumber(4)]));
        Assert.Equal(304, number(player, "started"));
        Assert.Equal(["select 3 4"], host.Calls);
    }

    [Fact]
    public void Archives_and_textures_written_from_scratch_round_trip()
    {
        var pixels = new byte[16 * 8 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 3]) = (255, 255);
        var nut = NutBuilder.FromRgba(16, 8, pixels);
        var texture = NutFile.Parse(nut).Textures[0];
        Assert.Equal((16, 8, NutPixelFormat.Bc3), (texture.Width, texture.Height, texture.Format));
        var decoded = NutTextureDecoder.DecodeRgba8(texture);
        Assert.Equal(255, decoded[0]);
        var builder = new LmbBuilder();
        var shape = builder.Shape(builder.Bounds(0, 0, 16, 8));
        shape.Quads.Add(new ShapeQuad([new(0, 0, 0, 0), new(16, 0, 1, 0), new(16, 8, 1, 1), new(0, 8, 0, 1)], (uint)builder.Texture(16, 8), ShapeQuad.Textured));
        var root = builder.Sprite();
        root.Frame(0).Add(new PlaceCommand(shape.Id, 1, 0, 1, 0, 1, 0, 0, PlaceCommand.MatrixPosition, (ushort)builder.Matrix(Affine.Identity), PlaceCommand.NoColor, PlaceCommand.NoColor));
        builder.RootId = root.Id;
        var lmb = builder.Build();
        var archive = ArchiveWriter.Write([new ArchiveMovie("test/test.lm", lmb, [nut])]);
        var view = DdpArchive.Open(archive).OpenMovie("test/test.lm");
        Assert.Equal(lmb, view.Data.ToArray());
        Assert.Single(view.Textures);
        var player = new LumenPlayer(parse(view.Data.ToArray(), 1), 1280, 720);
        Assert.Single(player.CreateRenderSnapshot().Quads);
    }

    private static string? originalSongSelect()
    {
        var path = Environment.GetEnvironmentVariable("LUMENSTUDIO_SONG_SELECT_DDP");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void Rewriting_with_identity_reproduces_every_original_action()
    {
        if (originalSongSelect() is not { } path) return; // game data is not redistributable
        var view = DdpArchive.Open(File.ReadAllBytes(path)).OpenMovie("song_select/song_select.lm");
        var movie = LmbSemanticReader.Read(LmbFile.Parse(view.Data)).Value;
        Assert.NotEmpty(movie.Actions);
        foreach (var action in movie.Actions)
            Assert.Equal(action.Bytecode.ToArray(), Avm1Rewriter.RemapStrings(action.Code, i => i));
        // A shifted pool (8-bit string pushes widen) still decodes to the same strings.
        foreach (var action in movie.Actions.Take(200))
        {
            var shifted = Avm1Rewriter.RemapStrings(action.Code, i => i + 300);
            var before = Avm1Assembly.StringReferences(action.Code).ToArray();
            var after = Avm1Assembly.StringReferences(Avm1Assembly.Decode(shifted)).ToArray();
            Assert.Equal(before.Select(i => i + 300), after);
        }
    }

    [Fact]
    public void Imported_original_sprites_play_in_a_movie_built_from_scratch()
    {
        if (originalSongSelect() is not { } path) return;
        var view = DdpArchive.Open(File.ReadAllBytes(path)).OpenMovie("song_select/song_select.lm");
        var source = LmbSemanticReader.Read(LmbFile.Parse(view.Data)).Value;
        var sizes = view.Textures.Select(t => NutFile.Parse(t.Data).Textures[0]).Select(t => ((int)t.Width, (int)t.Height)).ToList();
        var builder = new LmbBuilder();
        var importer = new MovieImporter(builder, source, sizes);
        var background = importer.Import("BackGround");
        var board = importer.Import("MusicBoard");
        var root = builder.Sprite();
        root.Frame(0).Add(new PlaceCommand(background, 1, (uint)builder.String("bg"), 1, 0, 1, 0, 0, PlaceCommand.MatrixPosition, (ushort)builder.Matrix(Affine.Identity), PlaceCommand.NoColor, PlaceCommand.NoColor));
        root.Frame(0).Add(new PlaceCommand(board, 2, (uint)builder.String("board"), 1, 0, 2, 0, 0, PlaceCommand.MatrixPosition, (ushort)builder.Matrix(Affine.Identity with { X = 640, Y = 360 }), PlaceCommand.NoColor, PlaceCommand.NoColor));
        builder.RootId = root.Id;
        var movie = parse(builder.Build(), builder.Textures.Count);
        Assert.True(importer.TextureMap.Count > 0 && importer.TextureMap.Count < sizes.Count);
        var player = new LumenPlayer(movie, 1280, 720);
        for (var i = 0; i < 30; i++) player.Advance();
        Assert.DoesNotContain(player.Diagnostics, d => d.Code is "LUM_CHARACTER_NOT_FOUND" or "LUM_MATRIX_NOT_FOUND" or "LUM_COLOR_NOT_FOUND");
        Assert.NotEmpty(player.CreateRenderSnapshot().Quads);
        Assert.Contains(movie.Sprites, s => s.CharacterId == board);
    }

    [Fact]
    public void The_song_select_sample_builds_and_plays_with_the_preview_host()
    {
        if (originalSongSelect() is not { } path) return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));
        var sample = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "song-select-poc", "song_select.lumen.json");
        var built = LumenStudio.Authoring.Project.ProjectCompiler.Compile(sample, game);
        var (movie, _) = LumenStudio.Authoring.Preview.PreviewMovie.Load(built.Archive, built.Project.Movie);
        var host = new LumenStudio.Authoring.Preview.PreviewSongSelectHost();
        var player = LumenStudio.Authoring.Preview.PreviewMovie.Start(movie, host);
        // Down twice (right Ka), Don on the song, left Ka to Hard, Don: the movie selects through Wd.
        var presses = new Dictionary<int, int> { [40] = 83, [50] = 83, [80] = 90, [100] = 65, [120] = 90 };
        for (var frame = 1; frame <= 140; frame++)
            player.Advance(new LumenInputSnapshot(presses.TryGetValue(frame, out var key) ? [key] : []));
        Assert.Contains("select 0 2 course 2", host.Log);
        // Without the Wd API (an older Waddamburo) the movie says so instead of showing an empty list.
        var bare = new LumenPlayer(movie, 1280, 720);
        for (var frame = 0; frame < 5; frame++) bare.Advance();
        Assert.StartsWith("This Song Select needs", bare.ReadScriptValue("genreText.text") as string);
        Assert.DoesNotContain(player.Diagnostics, d => d.Code is "LUM_AVM_STACK_UNDERFLOW" or "LUM_AVM_ATTACH_EXPORT_NOT_FOUND" or "LUM_AVM_FUNCTION_DEFERRED");
    }
}
