using System.Globalization;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace LumenStudio.Authoring.Preview;

/// <summary>
/// Waddamburo's Wd API over a made-up catalog (3 genres of 9 songs), so an authored Song Select runs in
/// LumenStudio's preview and lumen-render without the game. Calls the movie makes are logged.
/// </summary>
public sealed class PreviewSongSelectHost : ILumenHostBinding
{
    private static readonly string[] Genres = ["Pop", "Anime", "Namco Original"];
    private static readonly int[] GenreColors = [0x42C0FF, 0xFF8A3C, 0xFF4C9A];
    public List<string> Log { get; } = [];

    public void Install(LumenHostContext context) => context.RegisterObject("Wd", api =>
    {
        static int n(LumenHostCall call, int i) => i < call.Arguments.Length && call.Arguments[i].Kind == LumenHostValueKind.Number ? (int)call.Arguments[i].AsNumber() : -1;
        api.RegisterMethod("version", _ => LumenHostValue.FromNumber(1));
        api.RegisterMethod("ready", _ => LumenHostValue.FromBoolean(true));
        api.RegisterMethod("player", _ => LumenHostValue.FromNumber(0));
        api.RegisterMethod("genreCount", _ => LumenHostValue.FromNumber(Genres.Length));
        api.RegisterMethod("genreName", call => LumenHostValue.FromString(Genres[Math.Clamp(n(call, 0), 0, 2)]));
        api.RegisterMethod("genreColor", call => LumenHostValue.FromNumber(GenreColors[Math.Clamp(n(call, 0), 0, 2)]));
        api.RegisterMethod("songCount", _ => LumenHostValue.FromNumber(9));
        api.RegisterMethod("songTitle", call => LumenHostValue.FromString($"{Genres[Math.Clamp(n(call, 0), 0, 2)]} song {n(call, 1) + 1}"));
        api.RegisterMethod("songSubtitle", call => LumenHostValue.FromString($"from the test catalog"));
        api.RegisterMethod("songLevel", call => LumenHostValue.FromNumber(n(call, 2) switch { 0 => 2, 1 => 4, 2 => 6, 3 => 8 + n(call, 1) % 3, _ => 0 }));
        api.RegisterMethod("hasCourse", call => LumenHostValue.FromBoolean(n(call, 2) is >= 0 and <= 3));
        api.RegisterMethod("preview", call => { Log.Add($"preview {n(call, 0)} {n(call, 1)}"); return LumenHostValue.FromBoolean(true); });
        api.RegisterMethod("stopPreview", _ => LumenHostValue.Undefined);
        api.RegisterMethod("select", call => { Log.Add($"select {n(call, 0)} {n(call, 1)} course {n(call, 2)}"); return LumenHostValue.FromBoolean(true); });
        api.RegisterMethod("sound", call => { Log.Add($"sound {n(call, 0)} {n(call, 1)}"); return LumenHostValue.Undefined; });
        api.RegisterMethod("drum", call => { Log.Add($"drum {n(call, 0)}"); return LumenHostValue.Undefined; });
        api.RegisterMethod("log", call => { Log.Add("log " + string.Join(" ", call.Arguments.Select(a => a.Kind == LumenHostValueKind.Text ? a.AsString() : a.Kind == LumenHostValueKind.Number ? a.AsNumber().ToString(CultureInfo.InvariantCulture) : a.Kind.ToString()))); return LumenHostValue.Undefined; });
    });
}

/// <summary>Loads a built movie for playback outside the game.</summary>
public static class PreviewMovie
{
    public static (LmbMovieDefinition Movie, LumenSoftwareTexture[] Textures) Load(byte[] archive, string movieName)
    {
        var view = DdpArchive.Open(archive).OpenMovie(movieName);
        var movie = LmbSemanticReader.Read(LmbFile.Parse(view.Data)).Value;
        var textures = view.Textures.Select(t => NutFile.Parse(t.Data).Textures[0])
            .Select(t => new LumenSoftwareTexture(t.Width, t.Height, NutTextureDecoder.DecodeRgba8(t))).ToArray();
        return (movie, textures);
    }

    public static float StageWidth(LmbMovieDefinition movie) =>
        movie.Properties?.Words.Length > 8 ? BitConverter.UInt32BitsToSingle(movie.Properties.Words[8]) : 1280;

    public static float StageHeight(LmbMovieDefinition movie) =>
        movie.Properties?.Words.Length > 9 ? BitConverter.UInt32BitsToSingle(movie.Properties.Words[9]) : 720;

    /// <summary>A player with the host installed, told where the cursor starts as Waddamburo does (wdStart).</summary>
    public static LumenPlayer Start(LmbMovieDefinition movie, ILumenHostBinding host)
    {
        var player = new LumenPlayer(movie, StageWidth(movie), StageHeight(movie), hostBinding: host);
        if (player.CallbackNames.Contains("wdStart"))
            player.TryInvokeCallback("wdStart", [LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0)]);
        return player;
    }
}
