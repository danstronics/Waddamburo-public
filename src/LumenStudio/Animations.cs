using System.Text.RegularExpressions;
using Waddamburo.Formats.Lmb;

namespace LumenStudio;

/// <summary>One animation: a labelled frame range of a timeline (to the next label).</summary>
internal sealed record AnimationClip(string Label, string Variant, int Start, int End)
{
    public int Length => End - Start + 1;
}

/// <summary>
/// Animations sharing a name stem (in_1p / in_2p, select_on / select_off, loop1 / loop2) are one
/// animation with variants, chosen with a toggle instead of being played one after another.
/// </summary>
internal sealed record AnimationGroup(string Name, IReadOnlyList<AnimationClip> Variants)
{
    public override string ToString() => Variants.Count > 1 ? $"{Name}  ({Variants.Count} variants)" : $"{Name}  [{Variants[0].Start}–{Variants[0].End}]";

    public static IReadOnlyList<AnimationGroup> Build(LmbMovieDefinition movie, LmbSpriteDefinition sprite)
    {
        var frames = (int)Math.Max(1, sprite.DeclaredFrameCount);
        var labels = sprite.Timeline.OfType<LmbFrameLabelCommand>()
            .Select(l => (Frame: (int)l.Frame, Name: l.StringIndex < movie.Strings.Length ? movie.Strings[(int)l.StringIndex].Value : ""))
            .Where(l => l.Name.Length > 0 && l.Frame < frames)
            .OrderBy(l => l.Frame).ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
        if (labels.Count == 0) return [];
        var clips = new List<(string Name, int Start, int End)>();
        for (var i = 0; i < labels.Count; i++)
        {
            // Several labels on one frame share its range.
            var next = labels.Skip(i + 1).FirstOrDefault(l => l.Frame > labels[i].Frame);
            var end = next.Name is null ? frames - 1 : next.Frame - 1;
            clips.Add((labels[i].Name, labels[i].Frame, Math.Max(labels[i].Frame, end)));
        }
        var groups = new List<AnimationGroup>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stemOf in new Func<string, string?>[] { underscoreStem, digitStem })
            foreach (var family in clips.Where(c => !used.Contains(c.Name)).GroupBy(c => stemOf(c.Name)).Where(g => g.Key is not null && g.Count() > 1))
            {
                var stem = family.Key!;
                groups.Add(new AnimationGroup(stem, family.Select(c => new AnimationClip(c.Name, c.Name.Length > stem.Length ? c.Name[stem.Length..].TrimStart('_') : c.Name, c.Start, c.End)).ToArray()));
                foreach (var clip in family) used.Add(clip.Name);
            }
        foreach (var clip in clips.Where(c => used.Add(c.Name)))
            groups.Add(new AnimationGroup(clip.Name, [new AnimationClip(clip.Name, clip.Name, clip.Start, clip.End)]));
        return groups.OrderBy(g => g.Variants.Min(v => v.Start)).ThenBy(g => g.Name, StringComparer.Ordinal).ToArray();
    }

    private static string? underscoreStem(string name)
    {
        var index = name.LastIndexOf('_');
        return index > 0 && index < name.Length - 1 ? name[..index] : null;
    }

    private static string? digitStem(string name)
    {
        var match = Regex.Match(name, @"^(.*?[^\d])\d+$");
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>A named child sprite with states (labels) that scripts switch between.</summary>
internal sealed record ChildStates(string Path, string Display, IReadOnlyList<string> Labels, IReadOnlyList<AnimationClip> Segments)
{
    public AnimationClip Segment(string label) => Segments.First(s => s.Label == label);
}
