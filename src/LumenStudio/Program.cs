namespace LumenStudio;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        // A Lumen Author project opens the authoring window on its own.
        if (args.FirstOrDefault(a => a.EndsWith(".lumen.json", StringComparison.OrdinalIgnoreCase)) is { } movie)
        {
            var author = new AuthoringWindow(AppSettings.Load(), movie)
            {
                GameRootOverride = args.FirstOrDefault(a => a.StartsWith("--game=", StringComparison.Ordinal))?[7..],
            };
            if (args.FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.Ordinal)) is { } capture)
            {
                var frames = int.Parse(args.FirstOrDefault(a => a.StartsWith("--frames=", StringComparison.Ordinal))?[9..] ?? "60", System.Globalization.CultureInfo.InvariantCulture);
                // --keys=frame:code,... (Flash codes: 65 left Ka, 83 right Ka, 90 Don)
                var keys = (args.FirstOrDefault(a => a.StartsWith("--keys=", StringComparison.Ordinal))?[7..] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Split(':'))
                    .GroupBy(k => int.Parse(k[0], System.Globalization.CultureInfo.InvariantCulture))
                    .ToDictionary(g => g.Key, g => g.Select(k => int.Parse(k[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray());
                Ui.HeadlessErrorLog = Path.ChangeExtension(capture[10..], ".errors.txt");
                author.CaptureAfter(capture[10..], frames, keys);
            }
            Application.Run(author);
            return;
        }
        Application.Run(new MainWindow(args));
    }
}
