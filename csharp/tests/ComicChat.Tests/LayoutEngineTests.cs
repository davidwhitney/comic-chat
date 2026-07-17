using ComicChat.Core.Comic;
using ComicChat.Core.Geometry;
using Xunit;

namespace ComicChat.Tests;

public class LayoutEngineTests
{
    private static (UnitPanelPage page, AvatarDirectory dir, LayoutContext ctx) NewPage(int panelsPerRow = 2)
    {
        var measurer = new FakeTextMeasurer();
        var metrics = new PageMetrics
        {
            PanelsPerRow = panelsPerRow,
            UnitWidth = 2300,
            UnitHeight = 2300,
            BalloonFont = measurer.Font,
            WhisperFont = measurer.Font,
            ShoutFont = measurer.Font,
            TitleFont = measurer.Font,
        };

        var ctx = new LayoutContext(measurer, metrics);
        var dir = new AvatarDirectory();

        var page = new UnitPanelPage(ctx, dir)
        {
            BodyFactory = id => new FakeBody { AvatarId = id, Requested = true },
        };

        for (uint i = 1; i <= 4; i++)
            dir.GetOrAdd(i, _ => new FakeBody { AvatarId = i, Requested = false });

        return (page, dir, ctx);
    }

    [Fact]
    public void SingleUtteranceProducesOnePanelWithOneBalloon()
    {
        var (page, _, _) = NewPage();

        Assert.True(page.AddLine(1, "Hello there", BalloonMode.Say));

        var panel = Assert.Single(page.Panels);
        var balloon = Assert.Single(panel.Elements);
        Assert.Equal(1u, balloon.Speaker.AvatarId);
        Assert.Single(panel.Bodies);
    }

    [Fact]
    public void SameSpeakerTwiceNeverSharesAPanel()
    {
        // panel.cpp:1079 — pOldP->AvatarInPanel(uID) forces a new panel.
        var (page, _, _) = NewPage();

        page.AddLine(1, "First line", BalloonMode.Say);
        page.AddLine(1, "Second line", BalloonMode.Say);

        Assert.Equal(2, page.Panels.Count);
        Assert.All(page.Panels, p => Assert.Single(p.Elements));
    }

    [Fact]
    public void ActionModeAlwaysStartsANewPanel()
    {
        // panel.cpp:1064 — boxes get their own panel.
        var (page, _, _) = NewPage();

        page.AddLine(1, "Hello", BalloonMode.Say);
        page.AddLine(2, "waves at everyone", BalloonMode.Action);

        Assert.Equal(2, page.Panels.Count);
        Assert.IsType<WoodringBox>(page.Panels[^1].Elements[0]);
    }

    [Fact]
    public void ModesSelectTheRightBalloonSubclass()
    {
        var (page, _, _) = NewPage();

        Assert.IsType<WoodringNormal>(page.MakeBalloon("x", BalloonMode.Say));
        Assert.IsType<WoodringWhisper>(page.MakeBalloon("x", BalloonMode.Whisper));
        Assert.IsType<WoodringThink>(page.MakeBalloon("x", BalloonMode.Think));
        Assert.IsType<WoodringBox>(page.MakeBalloon("x", BalloonMode.Action));
    }

    [Fact]
    public void DifferentSpeakersCanShareAPanelOnceTheComicIsRunning()
    {
        // The first two panels are forced singletons by `m_panels.GetCount() < 2` (panel.cpp:1079),
        // so merging only becomes possible from the third utterance on.
        var (page, _, _) = NewPage();

        page.AddLine(1, "One", BalloonMode.Say);
        page.AddLine(2, "Two", BalloonMode.Say);
        page.AddLine(3, "Three", BalloonMode.Say);
        page.AddLine(4, "Four", BalloonMode.Say);

        Assert.Contains(page.Panels, p => p.Elements.Count > 1);
    }

    [Fact]
    public void LongTextSplitsAcrossPanelsRatherThanOverflowing()
    {
        var (page, _, _) = NewPage();

        var wall = string.Join(' ', Enumerable.Repeat("wordy", 200));
        page.AddLine(1, wall, BalloonMode.Say);

        Assert.True(page.Panels.Count > 1,
            $"expected the wall of text to spill across panels, got {page.Panels.Count}");
        Assert.All(page.Panels, p =>
            Assert.All(p.Elements, b => Assert.True(b.FInfo!.NLines <= FormatInfo.MaxLines)));
    }

    [Fact]
    public void BalloonsStayInTheTopHalfOfThePanel()
    {
        // GetBalloonRect (panel.cpp:839) gives balloons the top half only.
        var (page, _, ctx) = NewPage();

        page.AddLine(1, "Hello there friend", BalloonMode.Say);
        page.AddLine(2, "Well hello to you", BalloonMode.Say);
        page.AddLine(3, "And hello from me", BalloonMode.Say);

        foreach (var panel in page.Panels.Cast<UnitPanel>())
        {
            var free = panel.GetBalloonRect(ctx);
            foreach (var b in panel.Elements)
                Assert.True(b.GetCloudBBox().Bottom >= free.Bottom - BalloonMargin,
                    $"balloon bottom {b.GetCloudBBox().Bottom} escaped free rect bottom {free.Bottom}");
        }
    }

    private const int BalloonMargin = 400;

    [Fact]
    public void RouteRegionsNeverOverlap_SoTailsCannotCross()
    {
        // The core invariant of balloon.cpp's interval algebra: once every balloon in a panel
        // has been placed, no two tail corridors overlap, so no two tails can cross.
        var (page, _, _) = NewPage();

        for (uint i = 1; i <= 4; i++)
            page.AddLine(i, $"Message from {i}", BalloonMode.Say);

        foreach (var panel in page.Panels)
        {
            var regions = panel.Elements
                .Where(b => (b.GetElementType() & PeType.Box) == 0)
                .Select(b => b.RouteRgn)
                .ToList();

            for (int i = 0; i < regions.Count; i++)
                for (int j = i + 1; j < regions.Count; j++)
                {
                    bool disjoint = regions[i].Right <= regions[j].Left ||
                                    regions[j].Right <= regions[i].Left;
                    Assert.True(disjoint,
                        $"route regions overlap: {regions[i]} vs {regions[j]}");
                }
        }
    }

    [Fact]
    public void PanelSeedMakesLayoutReproducible()
    {
        // This is why m_seed exists: the whole comic is replayed from history on every
        // resize (pageview.cpp:1113) and must come out identical.
        static List<SRect> Run()
        {
            var (page, _, _) = NewPage();
            for (uint i = 1; i <= 3; i++)
                page.AddLine(i, $"Reproducible line number {i}", BalloonMode.Say);
            return page.Panels.SelectMany(p => p.Elements).Select(b => b.GetCloudBBox()).ToList();
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void EstablishingShotSuppressesZoomOnOpeningPanels()
    {
        // panel.cpp:788 — the comic opens wide, never on a close-up.
        var (page, _, ctx) = NewPage();
        page.AddLine(1, "Opening line", BalloonMode.Say);

        var panel = (UnitPanel)page.Panels[0];
        Assert.True(panel.Establishing);

        // With zoom suppressed the backdrop keeps its unzoomed logical width.
        Assert.Equal(ctx.Metrics.UnitWidth, panel.BackDrop.BBox.Width);
    }

    [Fact]
    public void PanelSeedsAreDrawnFromARandomStreamNotACounter()
    {
        // Regression: panel.cpp:556 does `m_seed = rand()`. Seeding panels from a counter
        // instead makes each panel's FIRST randfloat() tiny (the CRT's rand() returns 41 for
        // seed 1), and that first draw picks the balloon width — so every balloon collapses to
        // its minimum width and long lines split across panels for no reason.
        var (page, _, _) = NewPage();

        for (uint i = 1; i <= 4; i++)
            page.AddLine(i, $"Utterance number {i} with enough words to need a real width", BalloonMode.Say);

        var seeds = page.Panels.Select(p => p.Seed).ToList();
        Assert.True(seeds.Count >= 3, "need a few panels to judge the seed sequence");

        // Seeds must be draws from the CRT stream, not 1,2,3… A given draw may legitimately be
        // small (rand() really does return 41 first for seed 1), so assert the sequence shape
        // rather than the magnitude.
        var counter = Enumerable.Range(1, seeds.Count).Select(i => (uint)i);
        Assert.NotEqual(counter, seeds);

        Assert.All(seeds, s => Assert.InRange(s, 0u, (uint)CrtRandom.RandMax));
        Assert.Equal(seeds.Count, seeds.Distinct().Count());

        // The stream must actually advance: consecutive seeds are unrelated, not off-by-one.
        for (int i = 1; i < seeds.Count; i++)
            Assert.NotEqual(seeds[i - 1] + 1, seeds[i]);
    }

    [Fact]
    public void DocumentSeedMakesTheWholeComicReproducible()
    {
        // chatdoc.cpp:206 re-seeds from the document seed before replaying history.
        static List<uint> Run(uint docSeed)
        {
            var (page, _, _) = NewPage();
            page.DocumentSeed = docSeed;
            for (uint i = 1; i <= 3; i++)
                page.AddLine(i, $"Line {i}", BalloonMode.Say);
            return page.Panels.Select(p => p.Seed).ToList();
        }

        Assert.Equal(Run(12345), Run(12345));
        Assert.NotEqual(Run(12345), Run(99));
    }

    [Fact]
    public void PanelsCreatedAfterABackdropChangeStillGetTheBackdrop()
    {
        // Regression: the backdrop used to be stamped onto the panels that existed at the time,
        // so every panel created afterwards came out blank. It has to be page state, applied at
        // panel creation (the original's GetCurrentBackDropID, backdrop.cpp).
        var (page, _, _) = NewPage();

        page.AddLine(1, "Before the change", BalloonMode.Say);
        page.SetBackDrop(7);

        int existingPanels = page.Panels.Count;

        page.AddLine(2, "After the change", BalloonMode.Say);
        page.AddLine(3, "Later still", BalloonMode.Say);
        page.StartNewPanel();
        page.AddLine(4, "Later again", BalloonMode.Say);

        var freshPanels = page.Panels.Skip(existingPanels).ToList();
        Assert.NotEmpty(freshPanels);
        Assert.All(freshPanels, p => Assert.Equal(7, p.BackDrop.BackId));
    }

    [Fact]
    public void SetBackDropAlsoUpdatesPanelsThatAlreadyExist()
    {
        var (page, _, _) = NewPage();
        page.AddLine(1, "Hello", BalloonMode.Say);
        page.AddLine(2, "There", BalloonMode.Say);

        page.SetBackDrop(3, BackDropMode.NoZoom);

        Assert.All(page.Panels, p =>
        {
            Assert.Equal(3, p.BackDrop.BackId);
            Assert.Equal(BackDropMode.NoZoom, p.BackDrop.Mode);
        });
    }

    [Fact]
    public void GridPlacementWrapsAtPanelsPerRow()
    {
        var (page, _, ctx) = NewPage(panelsPerRow: 2);

        var (x0, y0) = page.GetPanelOrigin(0);
        var (x1, y1) = page.GetPanelOrigin(1);
        var (x2, y2) = page.GetPanelOrigin(2);

        Assert.Equal(y0, y1);                                   // same row
        Assert.Equal(x0 + ctx.Metrics.UnitWidth + ctx.Metrics.VInterstice, x1);
        Assert.Equal(x0, x2);                                   // wrapped
        Assert.True(y2 < y0);                                   // y is negative-down
    }
}
