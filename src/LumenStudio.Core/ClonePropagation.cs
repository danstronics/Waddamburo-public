namespace LumenStudio.Core;

/// <summary>One clone that will receive another texture's current content.</summary>
public sealed record SyncAction(TextureEntry Target, TextureEntry Source, DateTime SourceTimeUtc, bool OverwritesEdit);

/// <summary>
/// "Most recent edit wins": in every clone family whose members disagree, the member whose current
/// content was produced last (journal time, or the archive's file time for edits made elsewhere)
/// is copied to every other member that is not unlinked.
/// </summary>
public static class ClonePropagation
{
    public static IReadOnlyList<SyncAction> Plan(TextureIndex index, ArchiveStore store, Journal journal,
        ISet<string> unlinked, bool includeUniform = false)
    {
        var actions = new List<SyncAction>();
        var times = new Dictionary<ArchiveId, DateTime>();
        DateTime archiveTime(ArchiveId archive) =>
            times.TryGetValue(archive, out var time) ? time : times[archive] = store.CurrentTimestampUtc(archive);
        foreach (var family in index.Families)
        {
            if (family.Select(m => m.CurrentHash).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            if (!includeUniform && family[0].Uniform) continue;
            var edited = family.Where(m => index.IsEdited(m) && !unlinked.Contains(m.Id.Key)).ToArray();
            if (edited.Length == 0) continue;
            var winner = edited
                // Linked-layer textures that merely match an edit (not written by us) never outrank a real edit by file date.
                .Select(m => (Member: m, Time: journal.TimeOf(m.Id, m.CurrentHash) ?? (m.Modified ? archiveTime(m.Id.Archive) : DateTime.MinValue)))
                .OrderByDescending(p => p.Time).ThenBy(p => p.Member.Id.Archive.Layer == StudioProject.MainLayer ? 0 : 1)
                .ThenBy(p => p.Member.Id.Archive.Path, StringComparer.Ordinal).ThenBy(p => p.Member.Id.Index).First();
            foreach (var member in family)
            {
                if (member.CurrentHash == winner.Member.CurrentHash || unlinked.Contains(member.Id.Key)) continue;
                actions.Add(new SyncAction(member, winner.Member, winner.Time, index.IsEdited(member)));
            }
        }
        return actions;
    }
}
