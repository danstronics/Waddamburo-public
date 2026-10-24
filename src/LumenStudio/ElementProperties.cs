using System.ComponentModel;
using Waddamburo.Formats.Lmb;

namespace LumenStudio;

internal sealed class ElementProperties
{
    private LmbMatrix _matrix;
    private float _opacity;
    private readonly float _localWidth;
    private readonly float _localHeight;

    public ElementProperties(LmbPlaceObjectCommand placement, LmbEditDocument document, float width = 1, float height = 1)
    {
        CharacterId = placement.CharacterId;
        Depth = placement.Depth;
        Name = placement.NameStringIndex > 0 && placement.NameStringIndex < document.Definition.Strings.Length
            ? document.Definition.Strings[(int)placement.NameStringIndex].Value : "";
        Blend = placement.BlendMode;
        ClipDepth = placement.ClipDepth;
        _matrix = document.Matrix(placement);
        _opacity = document.Opacity(placement);
        _localWidth = Math.Max(0.001f, width);
        _localHeight = Math.Max(0.001f, height);
    }

    [Category("Element"), ReadOnly(true)] public uint CharacterId { get; }
    [Category("Element"), ReadOnly(true)] public ushort Depth { get; }
    [Category("Element")] public string Name { get; set; }
    [Category("Appearance"), Description("Hides using timeline alpha; scripts can override visibility at runtime.")]
    public bool Visible { get => _opacity > 0; set => _opacity = value ? (_opacity > 0 ? _opacity : 1) : 0; }
    [Category("Appearance"), Description("From 0 to 100.")]
    public float Opacity { get => _opacity * 100; set { if (!float.IsFinite(value) || value < 0 || value > 100) throw new ArgumentOutOfRangeException(nameof(value)); _opacity = value / 100; } }
    [Category("Appearance"), Description("0/1 normal, 3 multiply, 4 screen, 8 add.")]
    public ushort Blend { get; set; }
    [Category("Appearance"), Description("Last depth covered by the mask; 0 disables masking.")]
    public ushort ClipDepth { get; set; }
    [Category("Transform")] public float X { get => _matrix.X; set => _matrix = _matrix with { X = finite(value) }; }
    [Category("Transform")] public float Y { get => _matrix.Y; set => _matrix = _matrix with { Y = finite(value) }; }
    [Category("Transform"), Description("Size along the local axis. Changes element scale while preserving texture resolution.")]
    public float Width { get => ScaleX * _localWidth; set => ScaleX = finite(value) / _localWidth; }
    [Category("Transform"), Description("Size along the local axis; preserves NUT allocation.")]
    public float Height { get => ScaleY * _localHeight; set => ScaleY = finite(value) / _localHeight; }
    [Category("Transform"), Description("1 = 100%. Preserves axis direction and existing skew.")]
    public float ScaleX
    {
        get => MathF.Sqrt(_matrix.M11 * _matrix.M11 + _matrix.M12 * _matrix.M12);
        set { var old = ScaleX; var ratio = finite(value) / Math.Max(1e-8f, old); _matrix = _matrix with { M11 = old < 1e-8f ? value : _matrix.M11 * ratio, M12 = _matrix.M12 * ratio }; }
    }
    [Category("Transform")]
    public float ScaleY
    {
        get => MathF.Sqrt(_matrix.M21 * _matrix.M21 + _matrix.M22 * _matrix.M22);
        set { var old = ScaleY; var ratio = finite(value) / Math.Max(1e-8f, old); _matrix = _matrix with { M21 = _matrix.M21 * ratio, M22 = old < 1e-8f ? value : _matrix.M22 * ratio }; }
    }
    [Category("Transform"), Description("Degrees; rotates axes while preserving scale and skew.")]
    public float Rotation
    {
        get => MathF.Atan2(_matrix.M12, _matrix.M11) * 180 / MathF.PI;
        set
        {
            var angle = (finite(value) - Rotation) * MathF.PI / 180;
            var c = MathF.Cos(angle); var s = MathF.Sin(angle);
            _matrix = _matrix with { M11 = c * _matrix.M11 - s * _matrix.M12, M12 = s * _matrix.M11 + c * _matrix.M12,
                M21 = c * _matrix.M21 - s * _matrix.M22, M22 = s * _matrix.M21 + c * _matrix.M22 };
        }
    }
    [Category("Advanced matrix")] public float M11 { get => _matrix.M11; set => _matrix = _matrix with { M11 = finite(value) }; }
    [Category("Advanced matrix")] public float M12 { get => _matrix.M12; set => _matrix = _matrix with { M12 = finite(value) }; }
    [Category("Advanced matrix")] public float M21 { get => _matrix.M21; set => _matrix = _matrix with { M21 = finite(value) }; }
    [Category("Advanced matrix")] public float M22 { get => _matrix.M22; set => _matrix = _matrix with { M22 = finite(value) }; }
    [Browsable(false)] public LmbMatrix Matrix => _matrix;
    [Browsable(false)] public float Alpha => _opacity;
    private static float finite(float value) => float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}

/// <summary>Only endpoint fields that the animation baker actually consumes.</summary>
internal sealed class AnimationProperties(ElementProperties element)
{
    [Category("Transform")] public float X { get => element.X; set => element.X = value; }
    [Category("Transform")] public float Y { get => element.Y; set => element.Y = value; }
    [Category("Transform")] public float Width { get => element.Width; set => element.Width = value; }
    [Category("Transform")] public float Height { get => element.Height; set => element.Height = value; }
    [Category("Transform")] public float ScaleX { get => element.ScaleX; set => element.ScaleX = value; }
    [Category("Transform")] public float ScaleY { get => element.ScaleY; set => element.ScaleY = value; }
    [Category("Transform")] public float Rotation { get => element.Rotation; set => element.Rotation = value; }
    [Category("Appearance")] public float Opacity { get => element.Opacity; set => element.Opacity = value; }
    [Category("Advanced matrix")] public float M11 { get => element.M11; set => element.M11 = value; }
    [Category("Advanced matrix")] public float M12 { get => element.M12; set => element.M12 = value; }
    [Category("Advanced matrix")] public float M21 { get => element.M21; set => element.M21 = value; }
    [Category("Advanced matrix")] public float M22 { get => element.M22; set => element.M22 = value; }
    [Browsable(false)] public LmbMatrix Matrix => element.Matrix;
    [Browsable(false)] public float Alpha => element.Alpha;
}
