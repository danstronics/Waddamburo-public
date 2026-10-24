using System.Collections.Immutable;

namespace Waddamburo.Lumen.Rendering;

public readonly record struct LumenRenderVertex(float X, float Y, float U, float V);

public readonly record struct LumenRenderColor(float Red, float Green, float Blue, float Alpha)
{
    public static LumenRenderColor White { get; } = new(1f, 1f, 1f, 1f);

    public static LumenRenderColor Transparent { get; } = new(0f, 0f, 0f, 0f);
}

public enum LumenRenderBlend
{
    Normal,
    Add,
    Screen,
    Multiply,
}

public readonly record struct LumenNativeSurfaceKey
{
    public LumenNativeSurfaceKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct LumenNativeSurfacePlacement(float X, float Y, float Width, float Height)
{
    public static LumenNativeSurfacePlacement Centered(float width, float height) =>
        new(-width / 2, -height / 2, width, height);
}

/// <summary>Stencil writes never paint color. Depth is the reference before each operation.</summary>
public enum LumenRenderMaskOperation
{
    Draw,
    Push,
    Pop,
}

public readonly record struct LumenRenderQuad(
    uint TextureIndex,
    LumenRenderVertex TopLeft,
    LumenRenderVertex TopRight,
    LumenRenderVertex BottomRight,
    LumenRenderVertex BottomLeft,
    LumenRenderColor MultiplyColor,
    LumenRenderColor AddColor,
    LumenRenderBlend Blend = LumenRenderBlend.Normal,
    bool UseNearestSampling = false,
    LumenNativeSurfaceKey? NativeSurface = null,
    LumenRenderMaskOperation MaskOperation = LumenRenderMaskOperation.Draw,
    byte MaskDepth = 0,
    string? Source = null,
    float FrameBorderScale = 1,
    LumenQuadOrigin? Origin = null);

/// <summary>One display-list level that produced a quad: the instance's character, name, depth and frame.</summary>
public readonly record struct LumenInstanceRef(uint CharacterId, string Name, long Depth, uint PlacementId, int Frame);

/// <summary>
/// Editor picking data (recorded only when <c>LumenPlayer.RecordOrigins</c> is set): the instance path
/// from the root timeline to the drawing instance, and the shape geometry or text field drawn.
/// </summary>
public sealed record LumenQuadOrigin(
    System.Collections.Immutable.ImmutableArray<LumenInstanceRef> Path,
    uint CharacterId,
    int GeometryIndex,
    uint TextureIndex,
    LumenQuadOriginKind Kind);

public enum LumenQuadOriginKind
{
    Texture,
    NativeSurface,
    Text,
}

/// <summary>
/// Immutable renderer-independent output from one Lumen player state. Coordinates
/// use the movie's logical stage; commands retain display-list order.
/// </summary>
public sealed class LumenRenderSnapshot
{
    public LumenRenderSnapshot(float stageWidth, float stageHeight, IEnumerable<LumenRenderQuad> quads)
    {
        if (!float.IsFinite(stageWidth) || stageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageWidth));
        if (!float.IsFinite(stageHeight) || stageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(stageHeight));
        ArgumentNullException.ThrowIfNull(quads);

        StageWidth = stageWidth;
        StageHeight = stageHeight;
        Quads = quads.ToImmutableArray();
    }

    public float StageWidth { get; }

    public float StageHeight { get; }

    public ImmutableArray<LumenRenderQuad> Quads { get; }
}
