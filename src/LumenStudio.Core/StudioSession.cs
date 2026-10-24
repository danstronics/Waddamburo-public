using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Nut;

namespace LumenStudio.Core;

public enum Propagation
{
    /// <summary>Only the edited texture.</summary>
    None,
    /// <summary>Every linked clone, except uniform (blank / solid) images.</summary>
    Clones,
    /// <summary>Every linked clone, including uniform images.</summary>
    ClonesIncludingUniform,
}

public sealed record SaveReport(IReadOnlyList<ArchiveId> Written, IReadOnlyList<(ArchiveId Archive, string Message)> Failed,
    IReadOnlyList<(ArchiveId Archive, CompatibilityIssue Issue)> Issues);

/// <summary>
/// The editing session over a project: overlay reads, the clone index, pending (unsaved) edits with
/// undo/redo, clone propagation and saving to the overlay folders.
/// </summary>
public sealed class StudioSession
{
    private readonly List<ChangeSet> _undo = [];
    private readonly Stack<ChangeSet> _redo = [];
    private readonly LinkedList<(ArchiveId Id, byte[] Bytes)> _archiveCache = new();

    private StudioSession(StudioProject project)
    {
        Project = project;
        Store = new ArchiveStore(project);
        Journal = Journal.Load(project);
    }

    public StudioProject Project { get; }
    public ArchiveStore Store { get; }
    public Journal Journal { get; }
    public TextureIndex? Index { get; private set; }
    public ChangeSet Pending { get; private set; } = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after any pending change, undo/redo, save or index rebuild.</summary>
    public event Action? Changed;

    public static StudioSession Open(StudioProject project) => new(project);

    public void BuildIndex(IProgress<IndexProgress>? progress = null, CancellationToken cancellation = default)
    {
        Index = TextureIndex.Build(Store, progress, cancellation);
        Changed?.Invoke();
    }

    public TextureIndex RequireIndex() => Index ?? throw new InvalidOperationException("Build the texture index first (Textures › Scan clones).");

    /// <summary>The archive's saved current bytes (overlay or source), cached for a few archives.</summary>
    public byte[] ArchiveBytes(ArchiveId id)
    {
        for (var node = _archiveCache.First; node is not null; node = node.Next)
            if (node.Value.Id == id)
            {
                _archiveCache.Remove(node); _archiveCache.AddFirst(node);
                return node.Value.Bytes;
            }
        var bytes = Store.ReadCurrent(id);
        _archiveCache.AddFirst((id, bytes));
        while (_archiveCache.Count > 6) _archiveCache.RemoveLast();
        return bytes;
    }

    public byte[] TextureNut(TextureId id)
    {
        return TextureContainers.ReadTexture(ArchiveBytes(id.Archive), ArchiveStore.Kind(id.Archive), id.Index);
    }

    /// <summary>The texture as the user currently sees it: pending edit, or saved content.</summary>
    public RgbaImage TextureImage(TextureId id) =>
        Pending.Textures.TryGetValue(id, out var pending) ? pending.Image : RgbaImage.Decode(NutFile.Parse(TextureNut(id)).Textures[0]);

    public RgbaImage SourceTextureImage(TextureId id)
    {
        var nut = TextureContainers.ReadTexture(Store.ReadSource(id.Archive), ArchiveStore.Kind(id.Archive), id.Index);
        return RgbaImage.Decode(NutFile.Parse(nut).Textures[0]);
    }

    /// <summary>The movie's bytes including a pending edit.</summary>
    public byte[] MovieBytes(ArchiveId archive, string movie) =>
        Pending.Movies.TryGetValue((archive, movie), out var pending) ? pending
            : DdpArchive.Open(ArchiveBytes(archive)).OpenMovie(movie).Data.ToArray();

    /// <summary>Clones that a replacement of <paramref name="id"/> would also change.</summary>
    public IReadOnlyList<TextureEntry> PropagationTargets(TextureId id, Propagation propagation)
    {
        if (propagation == Propagation.None || Index is null) return [];
        var family = Index.Family(id);
        if (propagation == Propagation.Clones && family.Count > 0 && family[0].Uniform) return [];
        return family.Where(m => m.Id != id && !Project.Unlinked.Contains(m.Id.Key)).ToArray();
    }

    /// <summary>Replaces a texture (any size) and, optionally, every linked clone.</summary>
    public IReadOnlyList<TextureId> ReplaceTexture(TextureId id, RgbaImage image, Propagation propagation)
    {
        if (image.Width > NutTextureWriter.MaxDimension || image.Height > NutTextureWriter.MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(image), $"Textures are limited to {NutTextureWriter.MaxDimension}×{NutTextureWriter.MaxDimension}.");
        var now = DateTime.UtcNow;
        var targets = new List<TextureId> { id };
        targets.AddRange(PropagationTargets(id, propagation).Select(m => m.Id));
        change(set =>
        {
            foreach (var target in targets) set.Textures[target] = new PendingTexture(image, now, id);
        });
        return targets;
    }

    /// <summary>Restores the original (source) texture, optionally for every linked clone too.</summary>
    public void RevertTexture(TextureId id, Propagation propagation)
    {
        var targets = new List<TextureId> { id };
        targets.AddRange(PropagationTargets(id, propagation).Select(m => m.Id));
        var now = DateTime.UtcNow;
        change(set =>
        {
            foreach (var target in targets)
            {
                var nut = TextureContainers.ReadTexture(Store.ReadSource(target.Archive), ArchiveStore.Kind(target.Archive), target.Index);
                set.Textures[target] = new PendingTexture(RgbaImage.Decode(NutFile.Parse(nut).Textures[0]), now, target, nut);
            }
        });
    }

    public void SetMovie(ArchiveId archive, string movie, byte[] bytes)
    {
        if (Project.Layer(archive.Layer).ProtectMovies)
            throw new InvalidOperationException($"Movies in layer '{archive.Layer}' are protected. Unprotect the layer in Project settings to edit them.");
        change(set => set.Movies[(archive, movie)] = bytes);
    }

    public void DiscardMovie(ArchiveId archive, string movie) => change(set => set.Movies.Remove((archive, movie)));

    public IReadOnlyList<SyncAction> PlanSync(bool includeUniform = false) =>
        ClonePropagation.Plan(RequireIndex(), Store, Journal, Project.Unlinked, includeUniform);

    /// <summary>Queues a sync plan as pending edits; same-format, same-size clones are copied bit-exactly.</summary>
    public void ApplySync(IEnumerable<SyncAction> actions)
    {
        var list = actions.ToArray();
        var nuts = new Dictionary<TextureId, (byte[] Nut, RgbaImage Image)>();
        change(set =>
        {
            foreach (var action in list)
            {
                if (!nuts.TryGetValue(action.Source.Id, out var source))
                {
                    var nut = TextureNut(action.Source.Id);
                    nuts[action.Source.Id] = source = (nut, RgbaImage.Decode(NutFile.Parse(nut).Textures[0]));
                }
                set.Textures[action.Target.Id] = new PendingTexture(source.Image, action.SourceTimeUtc, action.Source.Id, source.Nut);
            }
        });
    }

    public void SetUnlinked(TextureId id, bool unlinked)
    {
        if (unlinked) Project.Unlinked.Add(id.Key); else Project.Unlinked.Remove(id.Key);
        Project.Save();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Pending);
        Pending = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(Pending);
        Pending = _redo.Pop();
        Changed?.Invoke();
    }

    /// <summary>Discards every pending edit of one archive.</summary>
    public void Discard(ArchiveId archive) => change(set => set.Remove(archive));

    /// <summary>Builds every affected archive, checks it, writes the overlay copies and records the journal.</summary>
    public SaveReport Save(IProgress<IndexProgress>? progress = null)
    {
        var written = new List<ArchiveId>();
        var failed = new List<(ArchiveId, string)>();
        var issues = new List<(ArchiveId, CompatibilityIssue)>();
        var archives = Pending.Archives.ToArray();
        var done = 0;
        foreach (var archive in archives)
        {
            progress?.Report(new IndexProgress(done++, archives.Length, archive.ToString()));
            try
            {
                var bytes = ArchiveBuilder.Build(Store, archive, Pending);
                var report = CompatibilityChecker.Check(bytes, Store.ReadSource(archive), ArchiveStore.Kind(archive));
                issues.AddRange(report.Select(i => (archive, i)));
                var errors = report.Where(i => i.Severity == IssueSeverity.Error).ToArray();
                if (errors.Length > 0) { failed.Add((archive, string.Join(Environment.NewLine, errors.Select(e => e.Message)))); continue; }
                Store.WriteOutput(archive, bytes);
                foreach (var (id, pending) in Pending.Textures.Where(p => p.Key.Archive == archive))
                    Journal.Record(Project, id, pending.Image, pending.TimeUtc, pending.Origin);
                written.Add(archive);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or ArgumentException
                or Waddamburo.Formats.IO.FormatReadException or NotSupportedException or UnauthorizedAccessException)
            {
                failed.Add((archive, exception.Message));
            }
        }
        if (written.Count > 0)
        {
            Journal.Save(Project);
            var remaining = Pending.Clone();
            foreach (var archive in written) remaining.Remove(archive);
            Pending = remaining;
            _undo.Clear(); _redo.Clear();
            _archiveCache.Clear();
            foreach (var archive in written) Index?.Refresh(archive);
        }
        progress?.Report(new IndexProgress(archives.Length, archives.Length, ""));
        Changed?.Invoke();
        return new SaveReport(written, failed, issues);
    }

    /// <summary>Removes an archive's overlay copy, restoring the original game file for it.</summary>
    public void RevertArchive(ArchiveId archive)
    {
        Store.RevertOutput(archive);
        Pending.Remove(archive);
        _archiveCache.Clear();
        Index?.Refresh(archive);
        Changed?.Invoke();
    }

    private void change(Action<ChangeSet> action)
    {
        var next = Pending.Clone();
        action(next);
        _undo.Add(Pending);
        while (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        Pending = next;
        Changed?.Invoke();
    }
}
