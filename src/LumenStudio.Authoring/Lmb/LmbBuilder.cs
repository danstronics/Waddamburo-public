using System.Buffers.Binary;
using System.Text;

namespace LumenStudio.Authoring.Lmb;

/// <summary>An affine transform as Lumen stores it: x' = A·x + C·y + X, y' = B·x + D·y + Y.</summary>
public readonly record struct Affine(float A, float B, float C, float D, float X, float Y)
{
    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static Affine Compose(float x, float y, float scaleX, float scaleY, float rotationDegrees)
    {
        var radians = rotationDegrees * MathF.PI / 180;
        var (sin, cos) = MathF.SinCos(radians);
        return new(cos * scaleX, sin * scaleX, -sin * scaleY, cos * scaleY, x, y);
    }
}

/// <summary>A colour transform entry; 256 is 1.0 for a multiply and one full channel for an add.</summary>
public readonly record struct ColorEntry(short R, short G, short B, short A);

public readonly record struct Vertex(float X, float Y, float U, float V);

/// <summary>One textured (or host-filled) quad of a shape.</summary>
public sealed record ShapeQuad(Vertex[] Vertices, uint Texture, uint Flags)
{
    /// <summary>Geometry flag of a quad drawn from its texture; 0 marks a fill the host provides.</summary>
    public const uint Textured = 0x10000;
}

public sealed class ShapeDefinition(uint id, int bounds)
{
    public uint Id { get; } = id;
    public int Bounds { get; } = bounds;
    public List<ShapeQuad> Quads { get; } = [];
}

/// <summary>A dynamic text field: the 16 header words of a DefineText record (character id first).</summary>
public sealed class TextDefinition(uint id, uint[] words)
{
    public uint Id { get; } = id;
    public uint[] Words { get; } = words;
}

public abstract record TimelineCommand;

/// <summary>PlaceObject: mode 1 places a new instance, 2 moves the one at its depth, 3 replaces it.</summary>
public sealed record PlaceCommand(
    uint Character,
    uint PlacementId,
    uint NameString,
    ushort Mode,
    ushort Blend,
    ushort Depth,
    ushort ClipDepth,
    ushort FirstFrame,
    ushort PositionKind,
    ushort PositionIndex,
    uint ColorMultiply,
    uint ColorAdd) : TimelineCommand
{
    public const ushort MatrixPosition = 0;
    public const ushort TranslationPosition = 0x8000;
    public const ushort NoPosition = ushort.MaxValue;
    public const uint NoColor = uint.MaxValue;
}

public sealed record RemoveCommand(uint Character, ushort Depth) : TimelineCommand;

public sealed record ActionCommand(uint Action) : TimelineCommand;

public sealed class SpriteDefinition(uint id)
{
    public uint Id { get; } = id;
    public string? ExportName { get; set; }
    public List<(string Name, int Frame)> Labels { get; } = [];
    public List<List<TimelineCommand>> Frames { get; } = [];

    /// <summary>Seek snapshots (F105) by frame: the display list a jump to that frame restores.</summary>
    public SortedDictionary<int, List<TimelineCommand>> KeyFrames { get; } = [];

    public List<TimelineCommand> Frame(int index)
    {
        while (Frames.Count <= index) Frames.Add([]);
        return Frames[index];
    }
}

/// <summary>
/// Writes a Lumen binary (LMB) from scratch. Pools are deduplicated; string 0 is the empty string.
/// Record order follows Green's movies: pools, properties, shapes, sprites (children first), texts.
/// </summary>
public sealed class LmbBuilder
{
    private readonly List<string> _strings = [""];
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal) { [""] = 0 };
    private readonly Pool<ColorEntry> _colors = new();
    private readonly Pool<Affine> _matrices = new();
    private readonly Pool<(float X, float Y)> _translations = new();
    private readonly Pool<(float Left, float Top, float Right, float Bottom)> _bounds = new();
    private readonly List<(int Width, int Height)> _textures = [];
    private readonly List<byte[]> _actions = [];
    private readonly List<ShapeDefinition> _shapes = [];
    private readonly List<TextDefinition> _texts = [];
    private readonly List<SpriteDefinition> _sprites = [];
    private uint _nextId = 1;

    public float Width { get; set; } = 1280;
    public float Height { get; set; } = 720;
    public float FrameRate { get; set; } = 60;
    public uint RootId { get; set; }

    /// <summary>Font table words (record 0x000A) copied from an imported movie; Waddamburo ignores it.</summary>
    public uint[]? FontWords { get; set; }

    public IReadOnlyList<string> Strings => _strings;
    public IReadOnlyList<(int Width, int Height)> Textures => _textures;
    public int ActionCount => _actions.Count;

    public uint NewCharacterId() => _nextId++;

    public int String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_stringIndex.TryGetValue(value, out var index)) return index;
        index = _strings.Count;
        _strings.Add(value);
        _stringIndex.Add(value, index);
        return index;
    }

    /// <summary>Appends a string without deduplication (keeps an imported pool's indices verbatim).</summary>
    public int AppendString(string value)
    {
        var index = _strings.Count;
        _strings.Add(value);
        _stringIndex.TryAdd(value, index);
        return index;
    }

    public int Color(ColorEntry color) => _colors.Add(color);
    public int Matrix(Affine matrix) => _matrices.Add(matrix);
    public int Translation(float x, float y) => _translations.Add((x, y));
    public int Bounds(float left, float top, float right, float bottom) => _bounds.Add((left, top, right, bottom));

    public int Texture(int width, int height)
    {
        _textures.Add((width, height));
        return _textures.Count - 1;
    }

    public int Action(byte[] bytecode)
    {
        _actions.Add(bytecode);
        return _actions.Count - 1;
    }

    public void SetAction(int index, byte[] bytecode) => _actions[index] = bytecode;

    public ShapeDefinition Shape(int bounds)
    {
        var shape = new ShapeDefinition(NewCharacterId(), bounds);
        _shapes.Add(shape);
        return shape;
    }

    public TextDefinition Text(uint[] words)
    {
        if (words.Length < 16) throw new ArgumentException("A DefineText record has 16 words.", nameof(words));
        var id = NewCharacterId();
        var copy = (uint[])words.Clone();
        copy[0] = id;
        var text = new TextDefinition(id, copy);
        _texts.Add(text);
        return text;
    }

    /// <summary>A dynamic text field over a box, as Green's own fields are laid out.</summary>
    public TextDefinition Text(float left, float top, float width, float height, float size, string align, string color,
        string font = "P_taiko_font")
    {
        var html = $"<p align=\"{align}\"><font face=\"{font}\" size=\"{size.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" color=\"{color}\" letterSpacing=\"0.000000\" kerning=\"1\"></font></p>";
        FontWords ??= [1, 0, 0, (uint)String(font), 0, 0];
        var words = new uint[16];
        words[2] = (uint)String(html);
        words[5] = (uint)Bounds(left, top, left + width, top + height);
        words[8] = 0x1FFFF; words[9] = 0x10001; words[10] = 0x10001;
        words[11] = BitConverter.SingleToUInt32Bits(size);
        words[15] = BitConverter.SingleToUInt32Bits(2);
        return Text(words);
    }

    public SpriteDefinition Sprite()
    {
        var sprite = new SpriteDefinition(NewCharacterId());
        _sprites.Add(sprite);
        return sprite;
    }

    public byte[] Build()
    {
        if (RootId == 0 || _sprites.All(s => s.Id != RootId)) throw new InvalidOperationException("The movie has no root sprite.");
        // Every string the records reference must be in the pool, which is written first.
        foreach (var sprite in _sprites)
        {
            if (sprite.ExportName is { Length: > 0 } name) String(name);
            foreach (var (label, _) in sprite.Labels) String(label);
        }
        using var output = new MemoryStream();
        var header = new byte[0x40];
        "LMB\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x10010100);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 2);
        output.Write(header);
        var words = new List<uint>();
        void record(uint tag)
        {
            Span<byte> head = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head, tag);
            BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)words.Count);
            output.Write(head);
            Span<byte> word = stackalloc byte[4];
            foreach (var value in words) { BinaryPrimitives.WriteUInt32BigEndian(word, value); output.Write(word); }
            words.Clear();
        }
        static uint f(float value) => BitConverter.SingleToUInt32Bits(value);
        static uint pack(int high, int low) => ((uint)(ushort)high << 16) | (ushort)low;

        // F001 strings: length, UTF-8, NUL, padded to a word.
        using (var pool = new MemoryStream())
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(number, (uint)_strings.Count); pool.Write(number);
            foreach (var value in _strings)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                BinaryPrimitives.WriteUInt32BigEndian(number, (uint)bytes.Length); pool.Write(number);
                pool.Write(bytes);
                for (var pad = (bytes.Length + 4) & ~3; pad > bytes.Length; pad--) pool.WriteByte(0);
            }
            rawRecord(output, 0xF001, pool.ToArray());
        }
        words.Add((uint)_colors.Items.Count);
        foreach (var c in _colors.Items) { words.Add(pack(c.R, c.G)); words.Add(pack(c.B, c.A)); }
        record(0xF002);
        words.Add((uint)_matrices.Items.Count);
        foreach (var m in _matrices.Items) words.AddRange([f(m.A), f(m.B), f(m.C), f(m.D), f(m.X), f(m.Y)]);
        record(0xF003);
        words.Add((uint)_translations.Items.Count);
        foreach (var t in _translations.Items) words.AddRange([f(t.X), f(t.Y)]);
        record(0xF103);
        words.Add((uint)_bounds.Items.Count);
        foreach (var b in _bounds.Items) words.AddRange([f(b.Left), f(b.Top), f(b.Right), f(b.Bottom)]);
        record(0xF004);
        using (var pool = new MemoryStream())
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(number, (uint)_actions.Count); pool.Write(number);
            foreach (var action in _actions)
            {
                BinaryPrimitives.WriteUInt32BigEndian(number, (uint)action.Length); pool.Write(number);
                pool.Write(action);
                for (var pad = (action.Length + 3) & ~3; pad > action.Length; pad--) pool.WriteByte(0);
            }
            rawRecord(output, 0xF005, pool.ToArray());
        }
        words.Add((uint)_textures.Count);
        for (var i = 0; i < _textures.Count; i++) words.AddRange([(uint)i, 0, f(_textures[i].Width), f(_textures[i].Height)]);
        record(0xF007);
        words.Add(0); record(0xF008);
        words.Add(0); record(0xF009);
        words.Add(0); record(0xF00A);
        words.AddRange(FontWords ?? [0]); record(0x000A);
        words.Add(1); record(0xF00B);
        words.AddRange([0, 1, 2, RootId, uint.MaxValue, RootId, 0x00290000, f(FrameRate), f(Width), f(Height), 0, 0]);
        record(0xF00C);
        words.AddRange([(uint)_shapes.Count, 0, (uint)_sprites.Count, 0, (uint)_texts.Count, 0, 0, 0]);
        record(0xF00D);

        foreach (var shape in _shapes)
        {
            words.AddRange([shape.Id, 0, (uint)shape.Bounds, (uint)shape.Quads.Count]);
            record(0xF022);
            foreach (var quad in shape.Quads)
            {
                if (quad.Vertices.Length != 4) throw new InvalidOperationException("Lumen geometry is four vertices.");
                foreach (var v in quad.Vertices) words.AddRange([f(v.X), f(v.Y), f(v.U), f(v.V)]);
                words.AddRange([quad.Texture, quad.Flags]);
                record(0xF023);
            }
        }
        foreach (var sprite in _sprites.OrderBy(s => s.Id == RootId ? 1 : 0).ThenBy(s => s.Id))
        {
            var depth = sprite.Frames.SelectMany(f => f).OfType<PlaceCommand>().Select(p => (int)p.Depth).DefaultIfEmpty(0).Max();
            var exportIndex = sprite.ExportName is { Length: > 0 } export ? (uint)String(export) : 0;
            words.AddRange([sprite.Id, 0, exportIndex, (uint)sprite.Labels.Count, (uint)Math.Max(1, sprite.Frames.Count),
                (uint)sprite.KeyFrames.Count, pack(Math.Max(1, depth), 0)]);
            record(0x0027);
            foreach (var (name, frame) in sprite.Labels) { words.AddRange([(uint)String(name), (uint)frame, 0]); record(0x002B); }
            void commands(List<TimelineCommand> list)
            {
                foreach (var command in list)
                {
                    switch (command)
                    {
                        case PlaceCommand p:
                            words.AddRange([p.Character, p.PlacementId, 0, p.NameString, pack(p.Mode, p.Blend), pack(p.Depth, p.ClipDepth),
                                pack(p.FirstFrame, 0), pack(p.PositionKind, p.PositionIndex), p.ColorMultiply, p.ColorAdd, 0, 0]);
                            record(0x0004);
                            break;
                        case RemoveCommand r:
                            words.AddRange([r.Character, pack(r.Depth, 0)]);
                            record(0x0005);
                            break;
                        case ActionCommand a:
                            words.AddRange([a.Action, 0]);
                            record(0x000C);
                            break;
                    }
                }
            }
            var frames = sprite.Frames.Count == 0 ? [[]] : sprite.Frames;
            for (var frame = 0; frame < frames.Count; frame++)
            {
                if (sprite.KeyFrames.TryGetValue(frame, out var snapshot))
                {
                    words.AddRange([(uint)frame, (uint)snapshot.Count]);
                    record(0xF105);
                    commands(snapshot);
                }
                words.AddRange([(uint)frame, (uint)frames[frame].Count]);
                record(0x0001);
                commands(frames[frame]);
            }
        }
        foreach (var text in _texts)
        {
            words.AddRange(text.Words);
            record(0x0025);
        }
        record(0xFF00);
        return output.ToArray();
    }

    private static void rawRecord(Stream output, uint tag, byte[] payload)
    {
        if ((payload.Length & 3) != 0) throw new InvalidOperationException("LMB payloads are whole words.");
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, tag);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)(payload.Length / 4));
        output.Write(head);
        output.Write(payload);
    }

    private sealed class Pool<T> where T : notnull
    {
        private readonly Dictionary<T, int> _index = [];
        public List<T> Items { get; } = [];

        public int Add(T value)
        {
            if (_index.TryGetValue(value, out var index)) return index;
            Items.Add(value);
            _index.Add(value, Items.Count - 1);
            return Items.Count - 1;
        }
    }
}
