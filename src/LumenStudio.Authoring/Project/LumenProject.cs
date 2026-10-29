using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumenStudio.Authoring.Project;

/// <summary>
/// A LumenStudio movie source (<c>*.lumen.json</c>): the stage, the archives to borrow symbols from,
/// images, symbols (imported, image, rectangle, text, sprite) and the root sprite. Paths are relative
/// to the project file; <c>${game}</c> is the game's USRDIR given at build time.
/// </summary>
public sealed class LumenProject
{
    /// <summary>The archive written, relative to lumendata/packed (e.g. song_select/packeddata.ddp).</summary>
    public string Archive { get; set; } = "";

    /// <summary>The movie's name inside the archive (e.g. song_select/song_select.lm).</summary>
    public string Movie { get; set; } = "";

    public float Width { get; set; } = 1280;
    public float Height { get; set; } = 720;
    public float Fps { get; set; } = 60;

    public List<ImportSource> Imports { get; set; } = [];
    public List<TextureSource> Textures { get; set; } = [];
    public List<SymbolSource> Symbols { get; set; } = [];

    /// <summary>The id of the sprite symbol the movie plays as its root.</summary>
    public string Root { get; set; } = "";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static LumenProject Load(string path) =>
        JsonSerializer.Deserialize<LumenProject>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} is not a LumenStudio project.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

/// <summary>An existing movie whose symbols (and their textures and scripts) can be borrowed.</summary>
public sealed class ImportSource
{
    public string Id { get; set; } = "";
    public string Archive { get; set; } = "";
    public string Movie { get; set; } = "";

    /// <summary>Drop the borrowed sprites' frame scripts (stop() stays): animation only, no original logic.</summary>
    public bool StripScripts { get; set; }
}

public sealed class TextureSource
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";

    /// <summary>bc1, bc2, bc3 (default) or argb.</summary>
    public string? Format { get; set; }
}

/// <summary>
/// A character. <c>import</c>: "importId:ExportName" or "importId:#characterId". <c>image</c>: a texture
/// (optionally a sub-rectangle) as a quad. <c>rect</c>: a solid rectangle. <c>text</c>: a dynamic text
/// field scripts fill through <c>text</c>. <c>sprite</c>: a timeline of layers, labels and scripts.
/// </summary>
public sealed class SymbolSource
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "sprite";

    // import
    public string? From { get; set; }

    // image / rect / text
    public string? Texture { get; set; }
    public float? Width { get; set; }
    public float? Height { get; set; }

    /// <summary>The symbol's origin as a fraction of its size (0,0 top-left; 0.5,0.5 centre).</summary>
    public float OriginX { get; set; }
    public float OriginY { get; set; }

    /// <summary>A sub-rectangle of the texture in pixels: [x, y, width, height].</summary>
    public float[]? Region { get; set; }

    /// <summary>rect fill or text colour, #RRGGBB or #AARRGGBB.</summary>
    public string? Color { get; set; }
    public float? Size { get; set; }
    public string? Align { get; set; }

    // sprite
    /// <summary>The linkage name attachMovie uses.</summary>
    public string? Export { get; set; }
    public int? Frames { get; set; }
    public Dictionary<string, int>? Labels { get; set; }
    public List<LayerSource>? Layers { get; set; }

    /// <summary>Frame scripts by frame number (0-based): LumenScript text, or "@file.as".</summary>
    public Dictionary<string, string>? Scripts { get; set; }
}

/// <summary>One depth of a sprite: a symbol placed from <see cref="From"/> to <see cref="To"/>, animated by keys.</summary>
public sealed class LayerSource
{
    /// <summary>The instance name scripts reach it by (this.name).</summary>
    public string? Name { get; set; }
    public string Symbol { get; set; } = "";
    public int? From { get; set; }
    public int? To { get; set; }

    /// <summary>add, multiply, screen... (Lumen blend ids); normal when omitted.</summary>
    public string? Blend { get; set; }
    public List<KeySource> Keys { get; set; } = [];
}

/// <summary>A keyframe; omitted values carry over from the previous key.</summary>
public sealed class KeySource
{
    public int Frame { get; set; }
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? ScaleX { get; set; }
    public float? ScaleY { get; set; }
    public float? Rotation { get; set; }
    public float? Alpha { get; set; }

    /// <summary>Multiply tint #RRGGBB.</summary>
    public string? Tint { get; set; }

    /// <summary>How this key eases to the next: linear (default), easeIn, easeOut, easeInOut, hold.</summary>
    public string? Tween { get; set; }
}
