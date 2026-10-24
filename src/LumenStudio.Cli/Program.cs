using System.Diagnostics;
using System.Globalization;
using LumenStudio.Core;

// LumenStudio command line: project setup, clone scanning, propagation and validation without the UI.
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        lumenstudio-cli new <project.lstudio> --base=<original USRDIR or packed> --output=<overlay packed folder>
        lumenstudio-cli scan <project.lstudio>                 Build the clone index and print statistics
        lumenstudio-cli sync <project.lstudio> [--apply] [--include-uniform]
                                                               Plan (or apply and save) most-recent-wins propagation
        lumenstudio-cli replace <project.lstudio> <archive> <texture> <image.png> [--no-propagate]
        lumenstudio-cli export <project.lstudio> <archive> <texture> <image.png>
        lumenstudio-cli link <project.lstudio> <name> <source folder> <output packed folder>
        lumenstudio-cli validate <project.lstudio>              Check every overlay archive for hardware invariants
        Archives are paths relative to the packed folder, e.g. song_select/packeddata.ddp.
        """);
    return 0;
}
string option(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..]
    ?? throw new ArgumentException($"--{name}= is required.");
var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet();
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
try
{
    switch (positional[0])
    {
        case "new":
        {
            var project = StudioProject.Create(positional[1], option("base"), option("output"));
            Console.WriteLine($"Created {project.Path}\n  original: {project.BasePacked}\n  overlay:  {project.OutputPacked}");
            return 0;
        }
        case "scan":
        {
            var session = open(positional[1]);
            var index = session.RequireIndex();
            var entries = index.Entries;
            var families = index.Families.ToArray();
            Console.WriteLine($"{entries.Count} textures, {entries.Count(e => e.Modified)} modified, {families.Length} clone families covering {families.Sum(f => f.Count)} textures");
            Console.WriteLine($"uniform families: {families.Count(f => f[0].Uniform)}; families with disagreeing members: {families.Count(f => f.Select(m => m.CurrentHash).Distinct().Count() > 1)}");
            var crossLayer = families.Where(f => f.Select(m => m.Id.Archive.Layer).Distinct().Count() > 1).ToArray();
            Console.WriteLine($"families spanning layers: {crossLayer.Length} ({crossLayer.Sum(f => f.Count)} textures)");
            foreach (var f in crossLayer.Where(f => !f[0].Uniform).Take(5))
                Console.WriteLine("  e.g. " + string.Join(" = ", f.Take(4).Select(m => $"{m.Id} {m.Name}")));
            var buckets = entries.Where(e => e.Modified).GroupBy(e => e.ChangedPixels switch { < 0 => "resized", 0 => "0", < 4 => "1-3", < 16 => "4-15", < 64 => "16-63", < 256 => "64-255", _ => "256+" })
                .Select(g => $"{g.Key}: {g.Count()}");
            Console.WriteLine("visibly changed texels in modified textures — " + string.Join(", ", buckets));
            foreach (var (archive, error) in index.Errors) Console.WriteLine($"  scan error {archive}: {error}");
            return 0;
        }
        case "sync":
        {
            var session = open(positional[1]);
            var plan = session.PlanSync(flags.Contains("--include-uniform"));
            foreach (var group in plan.GroupBy(a => a.Target.Id.Archive).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
                Console.WriteLine($"{group.Key}: {group.Count()} texture(s){(group.Any(a => a.OverwritesEdit) ? $", {group.Count(a => a.OverwritesEdit)} overwrite older edits" : "")}");
            if (flags.Contains("--verbose"))
                foreach (var action in plan)
                    Console.WriteLine($"  {action.Target.Id} {action.Target.Name} <- {action.Source.Id} {action.Source.Name} ({action.SourceTimeUtc:yyyy-MM-dd HH:mm}){(action.OverwritesEdit ? " OVERWRITES EDIT" : "")}");
            Console.WriteLine($"{plan.Count} texture(s) in {plan.Select(a => a.Target.Id.Archive).Distinct().Count()} archive(s) would receive newer clone content.");
            if (!flags.Contains("--apply") || plan.Count == 0) return 0;
            session.ApplySync(plan);
            return report(session.Save());
        }
        case "replace":
        {
            var session = open(positional[1]);
            var id = new TextureId(archiveId(positional[2]), int.Parse(positional[3], CultureInfo.InvariantCulture));
            var image = RgbaImage.FromPng(File.ReadAllBytes(positional[4]));
            var targets = session.ReplaceTexture(id, image, flags.Contains("--no-propagate") ? Propagation.None : Propagation.Clones);
            Console.WriteLine($"Replacing {targets.Count} texture(s) with a {image.Width}×{image.Height} image.");
            return report(session.Save());
        }
        case "export":
        {
            var session = StudioSession.Open(StudioProject.Load(positional[1]));
            var id = new TextureId(archiveId(positional[2]), int.Parse(positional[3], CultureInfo.InvariantCulture));
            File.WriteAllBytes(positional[4], session.TextureImage(id).ToPng());
            return 0;
        }
        case "link":
        {
            var project = StudioProject.Load(positional[1]);
            project.Linked.Add(new LinkedLayer { Name = positional[2], SourcePacked = PackedFolder.Resolve(positional[3]) ?? Path.GetFullPath(positional[3]), OutputPacked = Path.GetFullPath(positional[4]) });
            project.Validate();
            project.Save();
            Console.WriteLine($"Linked layer '{positional[2]}' (movies protected, textures follow clone propagation).");
            return 0;
        }
        case "validate":
        {
            var project = StudioProject.Load(positional[1]);
            var store = new ArchiveStore(project);
            var errors = 0;
            foreach (var archive in store.Archives().Where(store.IsModified))
            {
                var issues = CompatibilityChecker.Check(store.ReadCurrent(archive), store.ReadSource(archive), ArchiveStore.Kind(archive));
                errors += issues.Count(i => i.Severity == IssueSeverity.Error);
                Console.WriteLine($"{(issues.Any(i => i.Severity == IssueSeverity.Error) ? "FAIL" : "ok  ")} {archive}");
                foreach (var issue in issues) Console.WriteLine($"     {issue.Severity}: {issue.Message}");
            }
            return errors == 0 ? 0 : 1;
        }
        default:
            throw new ArgumentException($"Unknown command '{positional[0]}'.");
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine("error: " + exception.Message);
    return 2;
}

// "layer:path" names an archive in another layer, e.g. data:nutdata/cos_name/nutdatapack.ndp.
static ArchiveId archiveId(string text) => text.IndexOf(':') is var colon and > 1
    ? new ArchiveId(text[..colon], text[(colon + 1)..]) : new ArchiveId(StudioProject.MainLayer, text);

static StudioSession open(string path)
{
    var session = StudioSession.Open(StudioProject.Load(path));
    var clock = Stopwatch.StartNew();
    var last = -1;
    session.BuildIndex(new Progress<IndexProgress>(p =>
    {
        var percent = p.Done * 100 / Math.Max(1, p.Total);
        if (percent / 10 != last / 10) { last = percent; Console.Error.WriteLine($"  indexing {percent}% ({p.Done}/{p.Total})"); }
    }));
    Console.Error.WriteLine($"  index ready in {clock.Elapsed.TotalSeconds:0.0}s");
    return session;
}

static int report(SaveReport report)
{
    foreach (var archive in report.Written) Console.WriteLine($"written  {archive}");
    foreach (var (archive, message) in report.Failed) Console.WriteLine($"FAILED   {archive}: {message}");
    foreach (var (archive, issue) in report.Issues.Where(i => i.Issue.Severity != IssueSeverity.Info)) Console.WriteLine($"{issue.Severity,-8} {archive}: {issue.Message}");
    return report.Failed.Count == 0 ? 0 : 1;
}
