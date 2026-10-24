using System.Collections.Immutable;
using LumenStudio.Core;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace LumenStudio;

/// <summary>
/// A picked element: the instance path from the view root to the drawing instance, the selected
/// level in that path (1 = a direct child of the view root) and the quad that was hit.
/// </summary>
internal sealed record Selection(ImmutableArray<LumenInstanceRef> Path, int Level, LumenQuadOrigin? Hit)
{
    public LumenInstanceRef Node => Path[Level];
    public LumenInstanceRef Parent => Path[Level - 1];
    public Selection WithLevel(int level) => this with { Level = Math.Clamp(level, 1, Path.Length - 1) };

    public bool Contains(LumenQuadOrigin origin)
    {
        if (origin.Path.Length <= Level) return false;
        for (var i = 1; i <= Level; i++)
        {
            var a = origin.Path[i]; var b = Path[i];
            if (a.CharacterId != b.CharacterId || a.Depth != b.Depth || a.PlacementId != b.PlacementId) return false;
        }
        return true;
    }
}

/// <summary>The placement a selection edits: in <paramref name="Sprite"/>'s timeline at <paramref name="Frame"/>.</summary>
internal sealed record SelectedPlacement(uint Sprite, int Frame, LmbPlaceObjectCommand Placement, LumenMatrix ParentWorld);

/// <summary>
/// One open movie: its editable LMB document (mirrored into the session's pending changes), the
/// Lumen player that previews it, the movie's textures (including pending edits), playback and picking.
/// </summary>
internal sealed class MovieController
{
    private LumenPlayer? _player;
    private LumenRenderSnapshot? _snapshot;
    private PendingTexture?[] _textureSources = [];
    private LumenSoftwareTexture[] _textures = [];

    public MovieController(StudioSession studio, ArchiveId archive, string movie)
    {
        Studio = studio;
        Archive = archive;
        MovieName = movie;
        var entry = DdpArchive.Open(studio.ArchiveBytes(archive)).Index.GetMovie(movie);
        TextureBegin = entry.TextureBegin;
        TextureCount = entry.TextureEnd - entry.TextureBegin;
        Document = createDocument();
        ViewRoot = SceneRoot;
        reloadTextures(force: true);
    }

    public StudioSession Studio { get; }
    public ArchiveId Archive { get; }
    public string MovieName { get; }
    public int TextureBegin { get; }
    public int TextureCount { get; }
    public LmbEditDocument Document { get; private set; }
    public IReadOnlyList<LumenSoftwareTexture> Textures => _textures;
    public bool Protected => Studio.Project.Layer(Archive.Layer).ProtectMovies;

    public uint SceneRoot
    {
        get
        {
            var movie = Document.Definition;
            return movie.Properties?.RootCharacterId is uint id && movie.Sprites.Any(s => s.CharacterId == id) ? id
                : movie.Sprites.FirstOrDefault(s => s.DeclaredFrameCount > 0)?.CharacterId ?? throw new InvalidDataException("The movie has no sprite timeline.");
        }
    }

    /// <summary>The sprite the stage plays and the timeline shows: the scene root, or an entered sprite.</summary>
    public uint ViewRoot { get; private set; }
    public bool Isolated => ViewRoot != SceneRoot;
    public bool RunScripts { get; set; } = true;
    public bool Playing { get; private set; }
    public Selection? Selected { get; set; }

    public float StageWidth => stageWord(8, 1280);
    public float StageHeight => stageWord(9, 720);
    public float FrameRate => Document.Definition.Properties?.FrameRate is float rate && float.IsFinite(rate) && rate > 0 ? Math.Min(rate, 240) : 60;

    public LmbSpriteDefinition ViewSprite => Document.Definition.Sprites.Single(s => s.CharacterId == ViewRoot);
    public int FrameCount => (int)Math.Max(1, ViewSprite.DeclaredFrameCount);
    public int Frame => Player.CurrentFrame;
    public LumenPlayer Player => _player ??= createPlayer();
    public LumenRenderSnapshot Snapshot => _snapshot ??= Player.CreateRenderSnapshot();
    public IReadOnlyList<LumenRuntimeDiagnostic> Diagnostics => Player.Diagnostics;

    public event Action? DocumentChanged;
    public event Action? TexturesChanged;
    public event Action? FrameChanged;

    public string Title => $"{Archive} › {MovieName}";

    public string? StringAt(uint index) => index < Document.Definition.Strings.Length ? Document.Definition.Strings[(int)index].Value : null;

    public string SpriteName(uint id)
    {
        var sprite = Document.Definition.Sprites.FirstOrDefault(s => s.CharacterId == id);
        return sprite?.ExportStringIndex is uint name && StringAt(name) is { Length: > 0 } text ? text : $"sprite {id}";
    }

    public void Enter(uint spriteId)
    {
        if (!Document.Definition.Sprites.Any(s => s.CharacterId == spriteId && s.DeclaredFrameCount > 0))
            throw new InvalidOperationException("Only sprites with a timeline can be entered.");
        ViewRoot = spriteId; Selected = null; Playing = false;
        rebuild(0);
    }

    public void ExitToScene()
    {
        if (!Isolated) return;
        ViewRoot = SceneRoot; Selected = null; Playing = false;
        rebuild(0);
    }

    public void SetRunScripts(bool run)
    {
        RunScripts = run;
        rebuild(Frame);
    }

    public void Play()
    {
        Playing = true;
        Player.Play();
    }

    public void Pause()
    {
        Playing = false;
        _player?.Stop();
    }

    public void Seek(int frame)
    {
        frame = Math.Clamp(frame, 0, FrameCount - 1);
        Player.Seek(frame);
        if (!Playing) Player.Stop();
        invalidate();
    }

    public void Advance(int frames)
    {
        for (var i = 0; i < frames; i++) Player.Advance();
        invalidate();
    }

    /// <summary>Top-most visible element under a stage point, or null.</summary>
    public Selection? Pick(float x, float y)
    {
        var quads = Snapshot.Quads;
        var stageArea = StageWidth * StageHeight;
        for (var index = quads.Length - 1; index >= 0; index--)
        {
            var quad = quads[index];
            if (quad.MaskOperation != LumenRenderMaskOperation.Draw || quad.Origin is not { } origin || origin.Path.Length < 2) continue;
            if (!QuadGeometry.TryLocate(quad, x, y, out var u, out var v)) continue;
            var pickOnly = quad.MultiplyColor.Alpha <= 0 && origin.Kind != LumenQuadOriginKind.Texture;
            if (pickOnly)
            {
                // Invisible host placeholders: only small ones, so a full-screen slot never blocks the art beneath it.
                if (QuadGeometry.Area(quad) > stageArea * 0.35f) continue;
            }
            else if (origin.Kind == LumenQuadOriginKind.Texture)
            {
                if (quad.MultiplyColor.Alpha < 0.02f) continue;
                if (origin.TextureIndex < _textures.Length && QuadGeometry.Alpha(_textures[origin.TextureIndex], u, v) * quad.MultiplyColor.Alpha < 0.08f) continue;
            }
            return new Selection(origin.Path, origin.Path.Length - 1, origin);
        }
        return null;
    }

    /// <summary>Stage bounds of everything the selected instance draws in the current frame.</summary>
    public RectangleF? SelectionBounds(Selection? selection = null)
    {
        selection ??= Selected;
        if (selection is null) return null;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var quad in Snapshot.Quads)
        {
            if (quad.MaskOperation != LumenRenderMaskOperation.Draw || quad.Origin is not { } origin || !selection.Contains(origin)) continue;
            foreach (var p in new[] { quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft })
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        }
        return minX <= maxX ? RectangleF.FromLTRB(minX, minY, maxX, maxY) : null;
    }

    /// <summary>Movie-local texture indices drawn by the selection (or the whole frame) right now.</summary>
    public IReadOnlyList<int> TexturesUnder(Selection? selection)
    {
        var result = new List<int>();
        foreach (var quad in Snapshot.Quads)
            if (quad.MaskOperation == LumenRenderMaskOperation.Draw && quad.Origin is { Kind: LumenQuadOriginKind.Texture } origin
                && (selection is null || selection.Contains(origin)) && !result.Contains((int)origin.TextureIndex))
                result.Add((int)origin.TextureIndex);
        if (selection?.Hit is { Kind: LumenQuadOriginKind.Texture } hit && result.Remove((int)hit.TextureIndex))
            result.Insert(0, (int)hit.TextureIndex);
        return result;
    }

    public SelectedPlacement? Placement(Selection? selection = null)
    {
        selection ??= Selected;
        if (selection is null) return null;
        var parent = selection.Parent;
        var sprite = Document.Definition.Sprites.FirstOrDefault(s => s.CharacterId == parent.CharacterId);
        if (sprite is null || sprite.DeclaredFrameCount == 0 || selection.Node.AuthoredDepth is not ushort depth) return null;
        var frame = Math.Clamp(parent.Frame, 0, (int)sprite.DeclaredFrameCount - 1);
        var placement = Document.Placements(sprite.CharacterId, frame).FirstOrDefault(p => p.Depth == depth);
        return placement is null ? null : new SelectedPlacement(sprite.CharacterId, frame, placement, parent.World);
    }

    public string Describe(LumenInstanceRef node)
    {
        var kind = Document.Definition.Sprites.Any(s => s.CharacterId == node.CharacterId) ? "sprite"
            : Document.Definition.Texts.Any(t => t.CharacterId == node.CharacterId) ? "text" : "shape";
        return node.Name.Length > 0 ? $"{node.Name} ({kind} {node.CharacterId})" : $"{kind} {node.CharacterId}";
    }

    /// <summary>Applies an LMB edit, mirrors it into the session (undoable) and refreshes the preview.</summary>
    public void Edit(Action<LmbEditDocument> change)
    {
        if (Protected) throw new InvalidOperationException($"Layer '{Archive.Layer}' protects its movies; only textures can change. Unprotect it in Project › Settings.");
        var before = Document.Data;
        change(Document);
        if (Document.Data.Span.SequenceEqual(before.Span)) return;
        try { Studio.SetMovie(Archive, MovieName, Document.Data.ToArray()); }
        catch { Document = createDocument(); throw; }
        var frame = Frame;
        rebuild(frame);
        DocumentChanged?.Invoke();
    }

    /// <summary>Synchronises with the session after undo/redo, save or a texture edit.</summary>
    public void SyncWithSession()
    {
        var pending = Studio.MovieBytes(Archive, MovieName);
        if (!Document.Data.Span.SequenceEqual(pending))
        {
            Document = createDocument();
            rebuild(Math.Min(Frame, FrameCount - 1));
            DocumentChanged?.Invoke();
        }
        if (reloadTextures(force: false)) { invalidate(); TexturesChanged?.Invoke(); }
    }

    public TextureId GlobalTexture(int local) => new(Archive, TextureBegin + local);

    private LmbEditDocument createDocument()
    {
        var saved = DdpArchive.Open(Studio.ArchiveBytes(Archive)).OpenMovie(MovieName).Data.Span;
        var current = Studio.MovieBytes(Archive, MovieName);
        return new LmbEditDocument(current, saved);
    }

    private bool reloadTextures(bool force)
    {
        if (force || _textures.Length != TextureCount)
        {
            _textureSources = new PendingTexture?[TextureCount];
            _textures = new LumenSoftwareTexture[TextureCount];
            for (var i = 0; i < TextureCount; i++)
            {
                var id = GlobalTexture(i);
                _textureSources[i] = Studio.Pending.Textures.GetValueOrDefault(id);
                var image = Studio.TextureImage(id);
                _textures[i] = new LumenSoftwareTexture(image.Width, image.Height, image.Rgba);
            }
            return true;
        }
        var changed = false;
        for (var i = 0; i < TextureCount; i++)
        {
            var id = GlobalTexture(i);
            var pending = Studio.Pending.Textures.GetValueOrDefault(id);
            if (ReferenceEquals(pending, _textureSources[i])) continue;
            _textureSources[i] = pending;
            var image = Studio.TextureImage(id);
            _textures = (LumenSoftwareTexture[])_textures.Clone();
            _textures[i] = new LumenSoftwareTexture(image.Width, image.Height, image.Rgba);
            changed = true;
        }
        return changed;
    }

    private LumenPlayer createPlayer()
    {
        var movie = Document.Definition;
        var preview = RunScripts ? movie : movie with
        {
            Sprites = [.. movie.Sprites.Select(sprite => sprite with { Timeline = [.. sprite.Timeline.Where(c => c is not LmbDoActionCommand)] })],
        };
        var player = new LumenPlayer(preview, StageWidth, StageHeight, rootCharacterId: ViewRoot) { RecordOrigins = true, Name = MovieName };
        player.Stop();
        return player;
    }

    private void rebuild(int frame)
    {
        _player = null; _snapshot = null;
        var player = Player;
        if (frame > 0 && frame < FrameCount) player.Seek(frame);
        if (Playing) player.Play(); else player.Stop();
        invalidate();
        reselect();
    }

    private void invalidate()
    {
        _snapshot = null;
        FrameChanged?.Invoke();
    }

    /// <summary>Re-finds the selected instance after the player was rebuilt.</summary>
    private void reselect()
    {
        if (Selected is not { } selection) return;
        foreach (var quad in Snapshot.Quads)
            if (quad.Origin is { } origin && selection.Contains(origin))
            {
                Selected = selection with { Path = origin.Path, Hit = selection.Hit is null ? null : origin };
                return;
            }
    }

    private float stageWord(int word, float fallback)
    {
        var words = Document.Definition.Properties?.Words;
        if (words is not { } list || list.Length <= word) return fallback;
        var value = BitConverter.UInt32BitsToSingle(list[word]);
        return float.IsFinite(value) && value >= 16 && value <= 8192 ? value : fallback;
    }
}

/// <summary>Point-in-quad tests with texture coordinates, for picking.</summary>
internal static class QuadGeometry
{
    public static bool TryLocate(LumenRenderQuad quad, float x, float y, out float u, out float v) =>
        triangle(quad.TopLeft, quad.TopRight, quad.BottomRight, x, y, out u, out v)
        || triangle(quad.TopLeft, quad.BottomRight, quad.BottomLeft, x, y, out u, out v);

    private static bool triangle(LumenRenderVertex a, LumenRenderVertex b, LumenRenderVertex c, float x, float y, out float u, out float v)
    {
        u = v = 0;
        var d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(d) < 1e-9f) return false;
        var l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
        var l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
        var l3 = 1 - l1 - l2;
        const float epsilon = -1e-4f;
        if (l1 < epsilon || l2 < epsilon || l3 < epsilon) return false;
        u = l1 * a.U + l2 * b.U + l3 * c.U;
        v = l1 * a.V + l2 * b.V + l3 * c.V;
        return true;
    }

    public static float Area(LumenRenderQuad q) =>
        MathF.Abs((q.TopLeft.X * q.TopRight.Y - q.TopRight.X * q.TopLeft.Y) + (q.TopRight.X * q.BottomRight.Y - q.BottomRight.X * q.TopRight.Y)
            + (q.BottomRight.X * q.BottomLeft.Y - q.BottomLeft.X * q.BottomRight.Y) + (q.BottomLeft.X * q.TopLeft.Y - q.TopLeft.X * q.BottomLeft.Y)) / 2;

    public static float Alpha(LumenSoftwareTexture texture, float u, float v)
    {
        if (texture.Width == 0 || texture.Height == 0) return 0;
        var x = Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1);
        var y = Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1);
        return texture.Rgba.Span[(y * texture.Width + x) * 4 + 3] / 255f;
    }
}
