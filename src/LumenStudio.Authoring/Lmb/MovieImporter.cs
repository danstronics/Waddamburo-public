using Waddamburo.Formats.Lmb;

namespace LumenStudio.Authoring.Lmb;

/// <summary>
/// Copies characters of an existing movie (a sprite with everything it places: shapes, sprites, text
/// fields, frame scripts, labels, seek snapshots) into an <see cref="LmbBuilder"/>. Character ids,
/// pool entries, textures and the string indices inside imported bytecode are remapped; the source's
/// textures each character draws are reported through <see cref="TextureMap"/> so the caller can copy them.
/// </summary>
public sealed class MovieImporter
{
    private readonly LmbBuilder _builder;
    private readonly LmbMovieDefinition _source;
    private readonly Dictionary<uint, uint> _characters = [];
    private readonly Dictionary<int, int> _strings = [];
    private readonly Dictionary<uint, int> _actions = [];
    private readonly Dictionary<uint, LmbShapeDefinition> _shapes;
    private readonly Dictionary<uint, LmbSpriteDefinition> _sprites;
    private readonly Dictionary<uint, LmbTextDefinition> _texts;
    private readonly IReadOnlyList<(int Width, int Height)> _sourceTextures;

    public MovieImporter(LmbBuilder builder, LmbMovieDefinition source, IReadOnlyList<(int Width, int Height)> sourceTextures)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceTextures = sourceTextures;
        _shapes = source.Shapes.ToDictionary(s => s.CharacterId);
        _sprites = source.Sprites.ToDictionary(s => s.CharacterId);
        _texts = source.Texts.ToDictionary(t => t.CharacterId);
    }

    /// <summary>Source texture index → the builder's texture index, for every texture imported so far.</summary>
    public Dictionary<int, int> TextureMap { get; } = [];

    /// <summary>Drop frame scripts of imported sprites (keep only animation); stop() actions stay.</summary>
    public bool StripScripts { get; set; }

    public LmbMovieDefinition Source => _source;

    /// <summary>Finds a sprite by its export (linkage) name, or by "#id".</summary>
    public uint Find(string name)
    {
        if (name.StartsWith('#') && uint.TryParse(name.AsSpan(1), out var id))
            return _sprites.ContainsKey(id) || _shapes.ContainsKey(id) || _texts.ContainsKey(id) ? id
                : throw new KeyNotFoundException($"No character #{id} in the source movie.");
        foreach (var sprite in _source.Sprites)
            if (sprite.ExportStringIndex is uint index && index < _source.Strings.Length && _source.Strings[(int)index].Value == name)
                return sprite.CharacterId;
        throw new KeyNotFoundException($"No sprite exported as '{name}' in the source movie.");
    }

    public uint Import(string name) => Import(Find(name));

    public uint Import(uint sourceId)
    {
        if (_characters.TryGetValue(sourceId, out var existing)) return existing;
        if (_shapes.TryGetValue(sourceId, out var shape)) return importShape(shape);
        if (_texts.TryGetValue(sourceId, out var text)) return importText(text);
        if (_sprites.TryGetValue(sourceId, out var sprite)) return importSprite(sprite);
        throw new KeyNotFoundException($"Character {sourceId} is not defined in the source movie.");
    }

    public int ImportString(uint sourceIndex)
    {
        if (sourceIndex == 0 || sourceIndex >= _source.Strings.Length) return 0;
        if (_strings.TryGetValue((int)sourceIndex, out var mapped)) return mapped;
        mapped = _builder.Intern(_source.Strings[(int)sourceIndex].Value);
        _strings.Add((int)sourceIndex, mapped);
        return mapped;
    }

    public int Texture(int sourceIndex)
    {
        if (TextureMap.TryGetValue(sourceIndex, out var mapped)) return mapped;
        var (width, height) = sourceIndex < _sourceTextures.Count ? _sourceTextures[sourceIndex]
            : sourceIndex < _source.TextureBounds.Length
                // F007 entries are (index, 0, width, height).
                ? ((int)_source.TextureBounds[sourceIndex].Right, (int)_source.TextureBounds[sourceIndex].Bottom)
                : throw new InvalidDataException($"Texture {sourceIndex} is outside the source archive.");
        mapped = _builder.Texture(width, height);
        TextureMap.Add(sourceIndex, mapped);
        return mapped;
    }

    private uint importShape(LmbShapeDefinition shape)
    {
        var boundsIndex = shape.UninterpretedHeaderWords.Length > 1 ? (int)shape.UninterpretedHeaderWords[1] : -1;
        var target = _builder.Shape(bounds(boundsIndex));
        _characters.Add(shape.CharacterId, target.Id);
        foreach (var geometry in shape.Geometry)
        {
            var textured = (geometry.Flags & ShapeQuad.Textured) != 0;
            var texture = textured ? (uint)Texture((int)geometry.TextureIndex) : geometry.TextureIndex;
            target.Quads.Add(new ShapeQuad([.. geometry.Vertices.Select(v => new Vertex(v.X, v.Y, v.U, v.V))], texture, geometry.Flags));
        }
        return target.Id;
    }

    private uint importText(LmbTextDefinition text)
    {
        var words = text.Words.ToArray();
        words[2] = (uint)ImportString(words[2]);
        words[5] = (uint)bounds((int)words[5]);
        var target = _builder.Text(words);
        _characters.Add(text.CharacterId, target.Id);
        return target.Id;
    }

    private uint importSprite(LmbSpriteDefinition sprite)
    {
        // Children first: their ids precede the parent's, as in Green's movies.
        foreach (var place in sprite.Timeline.OfType<LmbPlaceObjectCommand>())
            if (defined(place.CharacterId) && !_characters.ContainsKey(place.CharacterId) && place.CharacterId != sprite.CharacterId)
                Import(place.CharacterId);
        var target = _builder.Sprite();
        _characters.Add(sprite.CharacterId, target.Id);
        if (sprite.ExportStringIndex is uint export && export < _source.Strings.Length)
            target.ExportName = _source.Strings[(int)export].Value;
        List<TimelineCommand>? current = null;
        foreach (var command in sprite.Timeline)
        {
            switch (command)
            {
                case LmbShowFrameCommand show:
                    current = target.Frame((int)show.Frame);
                    break;
                case LmbFrameKeyCommand key:
                    current = [];
                    target.KeyFrames[(int)key.Frame] = current;
                    break;
                case LmbFrameLabelCommand label when label.StringIndex < _source.Strings.Length:
                    target.Labels.Add((_source.Strings[(int)label.StringIndex].Value, (int)label.Frame));
                    break;
                case LmbPlaceObjectCommand place when current is not null:
                    // A move (mode 2) may name no character (0); keep it.
                    current.Add(new PlaceCommand(defined(place.CharacterId) ? Import(place.CharacterId) : place.CharacterId, place.PlacementId, (uint)ImportString(place.NameStringIndex),
                        place.Mode, place.BlendMode, place.Depth, place.ClipDepth, place.FirstFrame, place.PositionKind,
                        position(place.PositionKind, place.PositionIndex), color(place.ColorMultiplyIndex), color(place.ColorAddIndex)));
                    break;
                case LmbRemoveObjectCommand remove when current is not null:
                    current.Add(new RemoveCommand(_characters.GetValueOrDefault(remove.CharacterId), remove.Depth));
                    break;
                case LmbDoActionCommand action when current is not null:
                    if (this.action(action.ActionIndex) is int index) current.Add(new ActionCommand((uint)index));
                    break;
            }
        }
        target.Frame(Math.Max(0, (int)sprite.DeclaredFrameCount - 1));
        return target.Id;
    }

    private bool defined(uint id) => _shapes.ContainsKey(id) || _sprites.ContainsKey(id) || _texts.ContainsKey(id);

    private int? action(uint sourceIndex)
    {
        if (_actions.TryGetValue(sourceIndex, out var mapped)) return mapped;
        if (sourceIndex >= _source.Actions.Length) return null;
        var code = _source.Actions[(int)sourceIndex].Code;
        if (StripScripts && !code.Instructions.All(i => i.Opcode is 0x07 or 0x00)) return null;
        mapped = _builder.Action(Avm1Rewriter.RemapStrings(code, index => ImportString((uint)index)));
        _actions.Add(sourceIndex, mapped);
        return mapped;
    }

    private int bounds(int index) => index >= 0 && index < _source.Bounds.Length
        ? _builder.Bounds(_source.Bounds[index].Left, _source.Bounds[index].Top, _source.Bounds[index].Right, _source.Bounds[index].Bottom)
        : _builder.Bounds(0, 0, 0, 0);

    private ushort position(ushort kind, ushort index)
    {
        switch (kind)
        {
            case PlaceCommand.MatrixPosition when index < _source.Matrices.Length:
                var m = _source.Matrices[index];
                return checked((ushort)_builder.Matrix(new Affine(m.M11, m.M12, m.M21, m.M22, m.X, m.Y)));
            case PlaceCommand.TranslationPosition when index < _source.Translations.Length:
                return checked((ushort)_builder.Translation(_source.Translations[index].X, _source.Translations[index].Y));
            default:
                return index;
        }
    }

    private uint color(uint index)
    {
        if (index >= _source.ColorTransforms.Length) return index;
        var c = _source.ColorTransforms[(int)index];
        return (uint)_builder.Color(new ColorEntry(c.Red, c.Green, c.Blue, c.Alpha));
    }
}
