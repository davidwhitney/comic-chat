using Avalonia;
using Avalonia.Media.Imaging;
using ComicChat.App.Rendering;
using ComicChat.Core.Art;
using ComicChat.Core.Avatars;
using ComicChat.Core.Comic;
using ComicChat.Core.Semantics;

namespace ComicChat.App;

/// <summary>
/// Renders a scripted conversation to a PNG, with no window.
/// </summary>
/// <remarks>
/// This is the engine's end-to-end proof: it exercises the AVB loader, the expert system, the
/// layout engine and the renderer together and produces something you can actually look at.
/// It also mirrors the original's ability to save a comic to disk — the comic is a document,
/// not just a live view.
/// </remarks>
public static class ComicRenderer
{
    /// <summary>One scripted line of dialogue.</summary>
    public readonly record struct ScriptLine(string Nick, string Text, BalloonMode Mode = BalloonMode.Say);

    /// <summary>
    /// A demo conversation chosen to exercise the expert system's whole priority ladder:
    /// waves, self/other pointing, emoticons, laughter, shouting and an action box.
    /// </summary>
    public static readonly ScriptLine[] DemoScript =
    [
        new("Bolo", "Hi everyone! Welcome to Comic Chat."),
        new("Anna", "Hello Bolo :) I am glad to be here."),
        new("Kevin", "Are you sure this thing still works?"),
        new("Bolo", "I'm certain of it. LOL"),
        new("Anna", "THAT IS AMAZING!!!"),
        new("Kevin", "waves at the room", BalloonMode.Action),
        new("Bolo", "I think it looks pretty good."),
        new("Anna", "Don't you love a good comic? :)"),
    ];

    /// <summary>Build a comic from a script and write it to <paramref name="outputPath"/>.</summary>
    public static void RenderToPng(string outputPath, int widthDip = 1000, int panelsPerRow = 2,
                                   IReadOnlyList<ScriptLine>? script = null)
    {
        var session = BuildSession(out var bitmaps, out var backdrops, widthDip, panelsPerRow, script);
        RenderSession(session, bitmaps, backdrops, outputPath, widthDip);

        Console.WriteLine($"Wrote {outputPath} — {session.Page.Panels.Count} panels, " +
                          $"{widthDip}x?, {session.Participants.Count} participants.");

        if (Environment.GetEnvironmentVariable("COMIC_DEBUG") == "1")
            DumpLayout(session);
    }

    /// <summary>
    /// End-to-end check of the history subsystem: build a comic, replay it, save it, reload it,
    /// and confirm each stage reproduces the same strip.
    /// </summary>
    /// <remarks>
    /// This is the check that matters for history. Unit tests prove the records round-trip; only
    /// driving the whole pipeline proves that a replayed or reloaded comic is actually the same
    /// comic, since layout depends on per-panel seeds and staging hysteresis that a naive replay
    /// would get wrong.
    /// </remarks>
    public static int VerifyHistory(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        int failures = 0;

        var session = BuildSession(out var bitmaps, out var backdrops, 1000, 2);
        var before = Fingerprint(session);
        Console.WriteLine($"built:     {session.Page.Panels.Count} panels, fingerprint {before.Length} entries");

        // 1. A reload at the same size must reproduce the comic exactly.
        session.Reload();
        var afterReload = Fingerprint(session);
        if (!before.SequenceEqual(afterReload))
        {
            Console.Error.WriteLine("FAIL: reload at the same panel size changed the comic.");
            failures++;
        }
        else Console.WriteLine("ok:        reload at same size is identical");

        // 2. Every panel must carry the backdrop, including ones created after the change.
        var blank = session.Page.Panels.Count(p => p.BackDrop.BackId == 0);
        if (blank > 0)
        {
            Console.Error.WriteLine($"FAIL: {blank} panel(s) have no backdrop.");
            failures++;
        }
        else Console.WriteLine($"ok:        all {session.Page.Panels.Count} panels have a backdrop");

        // 3. Save, reload into a fresh session, and compare the resulting strip.
        var cccPath = Path.Combine(outputDir, "verify.ccc");
        session.History.Save(cccPath);

        var reloaded = BuildSession(out var bitmaps2, out var backdrops2, 1000, 2, script: []);
        reloaded.History.Clear();
        reloaded.ResetComic();
        if (!reloaded.History.Load(cccPath, out var unknown))
        {
            Console.Error.WriteLine("FAIL: could not load the archive we just wrote.");
            return failures + 1;
        }
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: unknown keywords on reload: {string.Join(",", unknown)}");
            failures++;
        }

        var afterLoad = Fingerprint(reloaded);
        if (!before.SequenceEqual(afterLoad))
        {
            Console.Error.WriteLine("FAIL: comic loaded from .ccc differs from the original.");
            Console.Error.WriteLine($"  original: {before.Length} balloons, loaded: {afterLoad.Length}");
            foreach (var (a, b) in before.Zip(afterLoad).Where(p => p.First != p.Second).Take(3))
                Console.Error.WriteLine($"  {a}\n  {b}");
            failures++;
        }
        else Console.WriteLine($"ok:        .ccc round-trip reproduces the comic ({afterLoad.Length} balloons)");

        RenderSession(reloaded, bitmaps2, backdrops2, Path.Combine(outputDir, "reloaded.png"), 1000);

        // 4. A replay at a different panel size must still produce a valid comic.
        session.Ctx.Metrics.SetPanelsWide(3, (int)(1200 * AvaloniaTextMeasurer.TwipsPerDip));
        session.Reload();
        if (session.Page.Panels.Count == 0)
        {
            Console.Error.WriteLine("FAIL: replay at a new panel size produced no panels.");
            failures++;
        }
        else Console.WriteLine($"ok:        replay at a new panel size produced {session.Page.Panels.Count} panels");

        RenderSession(session, bitmaps, backdrops, Path.Combine(outputDir, "resized.png"), 1200);

        failures += VerifyPoses();
        RenderWheelDemo(Path.Combine(outputDir, "wheel-pose.png"));

        Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
        return failures;
    }

    /// <summary>
    /// Render one character across three lines to show the wheel/freeze lifecycle: a normal line,
    /// then a line with an ANGRY pose pinned on the wheel, then a line after the temporary freeze
    /// has expired (which should auto-pose from the text again).
    /// </summary>
    private static void RenderWheelDemo(string outputPath)
    {
        var art = ArtLibrary.Load();
        var measurer = new AvaloniaTextMeasurer();
        var font = measurer.CreateFontInfo();
        var metrics = new PageMetrics { BalloonFont = font, WhisperFont = font, ShoutFont = font, TitleFont = font };
        metrics.SetPanelsWide(3, (int)(1400 * AvaloniaTextMeasurer.TwipsPerDip));

        var session = new ComicSession(new LayoutContext(measurer, metrics), art.Avatars)
        {
            BackDropIdByName = name =>
            {
                int i = art.Backdrops.FindIndex(b => b.name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? (ushort)0 : (ushort)(i + 1);
            },
        };

        var bitmaps = new ArtBitmapCache();
        var backdrops = new BackDropRenderer(bitmaps);
        for (int i = 0; i < art.Backdrops.Count; i++)
            backdrops.Register((ushort)(i + 1), art.Backdrops[i].backdrop);

        session.RecordAvatarChange("Bolo", "Bolo");
        if (art.Backdrops.Count > 0) session.RecordBackDrop(art.Backdrops[0].name);

        var bolo = session.Participants[session.ByNick("Bolo")!.Id].Resolver;

        session.Say("Bolo", "Just a normal hello.");

        // The user drags the wheel to ANGRY before the next line: apply the pose and temp-freeze.
        bolo.UpdateBody(bolo.GetBodyFromEmotion(new Emotion(1.0, Em.Angry)));
        bolo.Freeze = AvatarFreezeState.TempFrozen;
        session.Say("Bolo", "Now I am really annoyed!");

        // The temp freeze has expired; this line auto-poses from its text again.
        session.Say("Bolo", "But now I have calmed down.");

        RenderSession(session, bitmaps, backdrops, outputPath, 1400);
        Console.WriteLine($"wrote:     {Path.GetFileName(outputPath)} (wheel-pose lifecycle demo)");
    }

    /// <summary>
    /// Confirms auto-detection produces varied poses and a wheel pose reaches the drawn strip.
    /// </summary>
    private static int VerifyPoses()
    {
        int failures = 0;
        var art = ArtLibrary.Load();

        // 1. The expert system infers different faces/torsos for different text.
        var complex = art.Avatars.OfType<AvatarComplex>().FirstOrDefault(a => a.Faces.Count > 4);
        if (complex is null)
        {
            Console.Error.WriteLine("FAIL: no complex avatar with enough faces to test poses.");
            return failures + 1;
        }

        var tp = new TextPose();
        var seen = new HashSet<(int, int)>();
        foreach (var msg in new[] { "hello there", "LOL so funny", ":(", "I am here", "Are you sure?", "STOP IT" })
        {
            var r = new AvatarPoseResolver(complex, complex.PoseStyle);
            var body = tp.ChatPreSendText(msg, r);
            if (body is { } b) seen.Add((b.FaceIndex, b.TorsoIndex));
        }
        if (seen.Count < 4)
        {
            Console.Error.WriteLine($"FAIL: auto-detection produced only {seen.Count} distinct poses across 6 lines.");
            failures++;
        }
        else Console.WriteLine($"ok:        auto-detection gave {seen.Count} distinct poses across 6 lines");

        // 2. A wheel pose survives a temp freeze for one line, then auto-posing resumes.
        var res = new AvatarPoseResolver(complex, complex.PoseStyle);
        var pinned = res.GetBodyFromEmotion(new Emotion(1.0, Em.Laugh));
        res.UpdateBody(pinned);
        res.Freeze = AvatarFreezeState.TempFrozen;

        bool held = tp.ChatPreSendText("plain words", res) is null
                    && res.CurrentBody.FaceIndex == pinned.FaceIndex;
        res.ResetAvatar();
        bool resumed = res.Freeze == AvatarFreezeState.Unfrozen
                       && tp.ChatPreSendText("Are you sure?", res) is not null;

        if (held && resumed)
            Console.WriteLine("ok:        wheel pose holds one line, then auto-posing resumes");
        else
        {
            Console.Error.WriteLine($"FAIL: wheel/freeze lifecycle broken (held={held}, resumed={resumed}).");
            failures++;
        }

        return failures;
    }

    /// <summary>A stable description of the laid-out comic, for comparing two runs.</summary>
    private static string[] Fingerprint(ComicSession s) =>
        [.. s.Page.Panels.SelectMany((p, i) => p.Elements.Select(b =>
            $"p{i} seed={p.Seed} back={p.BackDrop.BackId} bodies={p.Bodies.Count} " +
            $"cloud={b.GetCloudBBox()} lines={b.FInfo?.NLines} text=\"{b.Str}\""))];

    private static ComicSession BuildSession(out ArtBitmapCache bitmaps, out BackDropRenderer backdrops,
                                             int widthDip, int panelsPerRow,
                                             IReadOnlyList<ScriptLine>? script = null)
    {
        script ??= DemoScript;

        var art = ArtLibrary.Load();
        if (art.Avatars.Count == 0)
            throw new InvalidOperationException("No .avb art found — expected v2.5-beta-1/comicart.");

        var measurer = new AvaloniaTextMeasurer();
        var font = measurer.CreateFontInfo();
        var metrics = new PageMetrics
        {
            BalloonFont = font,
            WhisperFont = font,
            ShoutFont = font,
            TitleFont = font,
        };

        // Size the panels to the output, exactly as the original sized them to the window
        // (SetPanelsWide, pageview.cpp:1101). Leaving them at the 2300-twip minimum while
        // scaling the view down instead would shrink the panel relative to the font, so only
        // a line or two of text would fit and every balloon would split.
        metrics.SetPanelsWide(panelsPerRow, (int)(widthDip * AvaloniaTextMeasurer.TwipsPerDip));

        var session = new ComicSession(new LayoutContext(measurer, metrics), art.Avatars);

        bitmaps = new ArtBitmapCache();
        var bd = new BackDropRenderer(bitmaps);
        backdrops = bd;
        for (int i = 0; i < art.Backdrops.Count; i++)
            bd.Register((ushort)(i + 1), art.Backdrops[i].backdrop);

        session.BackDropIdByName = name =>
        {
            int i = art.Backdrops.FindIndex(b =>
                b.name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? (ushort)0 : (ushort)(i + 1);
        };

        // Give each speaker a distinct character, by name where the art pack has one.
        //
        // This has to go through history as a `changeavatar` record, not a direct GetOrCreate:
        // who appears as whom decides each character's art, hence their width, hence their tail
        // attach point, hence where the balloons land. An archive that omits it reloads with
        // round-robin art and every balloon shifts.
        foreach (var nick in script.Select(s => s.Nick).Distinct())
            session.RecordAvatarChange(nick, nick);

        // Put everyone in a room rather than on a blank page. Recorded in history so the
        // backdrop reaches panels created later, survives a reload, and lands in an archive.
        if (art.Backdrops.Count > 0)
            session.RecordBackDrop(art.Backdrops[0].name);

        foreach (var line in script)
            session.Say(line.Nick, line.Text, line.Mode);

        return session;
    }

    private static void RenderSession(ComicSession session, ArtBitmapCache bitmaps,
                                      BackDropRenderer backdrops, string outputPath, int widthDip)
    {
        var view = new ComicPageView();
        view.SetPage(session.Page, new AvatarBodyRenderer(bitmaps), backdrops);

        // Panels are already sized in TWIPS for this output, so the view draws 1:1.
        view.Scale = AvaloniaTextMeasurer.TwipsPerDip;

        int heightDip = (int)Math.Ceiling(view.ComicHeight);
        var size = new Size(widthDip, Math.Max(heightDip, 100));

        view.Measure(size);
        view.Arrange(new Rect(size));

        var target = new RenderTargetBitmap(new PixelSize(widthDip, (int)size.Height), new Vector(96, 96));
        target.Render(view);

        using var file = File.Create(outputPath);
        target.Save(file, new PngBitmapEncoderOptions());
    }

    private static void DumpLayout(ComicSession session)
    {
        var m = session.Ctx.Metrics;
        var font = m.BalloonFont;
        Console.WriteLine($"font: size={font.FontSize}tw lineHeight={font.LineHeight}tw " +
                          $"panel={m.UnitWidth}x{m.UnitHeight}tw");

        for (int i = 0; i < session.Page.Panels.Count; i++)
        {
            var p = (UnitPanel)session.Page.Panels[i];
            var free = p.GetBalloonRect(session.Ctx);
            var poses = string.Join(", ", p.Bodies.Select(b =>
                b is AvatarBody ab ? $"{ab.AvatarId}:f{ab.FaceIndex}/t{ab.TorsoIndex}" : "?"));
            Console.WriteLine($"panel[{i}] bodies={p.Bodies.Count} balloons={p.Elements.Count} " +
                              $"back={p.BackDrop.BackId} poses=[{poses}] establishing={p.Establishing}");
            foreach (var b in p.Elements)
            {
                Console.WriteLine($"   balloon w={b.BBox.Width} lines={b.FInfo?.NLines} " +
                                  $"cloud={b.GetCloudBBox()} maxLineW={b.FInfo?.MaxWidth}");
                for (int l = 0; l < (b.FInfo?.NLines ?? 0); l++)
                    Console.WriteLine($"      [{l}] w={b.FInfo!.Widths[l]} \"{b.FInfo.Starts[l]}\"");
            }
        }
    }
}
