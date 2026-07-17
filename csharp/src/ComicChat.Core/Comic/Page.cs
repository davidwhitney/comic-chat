using ComicChat.Core.Geometry;

namespace ComicChat.Core.Comic;

/// <summary>Balloon mode bitmask. Port of BM_* (defines.h:63).</summary>
[Flags]
public enum BalloonMode : ushort
{
    Say = 0x0001,
    Whisper = 0x0002,
    Think = 0x0004,
    Action = 0x0008,
    Sound = 0x0010,
    Away = 0x0020,
    HeresInfo = 0x0040,
    NoFormat = 0x0080,
    ExChan = 0x0100,
}

/// <summary>Wire mode ordinal. Port of SM_* (defines.h:57).</summary>
public enum SendMode
{
    Say = 1,
    Whisper = 2,
    Think = 3,
    Shout = 4,
    Action = 5,
}

/// <summary>A page of panels. Port of CPage (panel.h:69).</summary>
public abstract class Page
{
    public List<Panel> Panels { get; } = [];
    public SRect Boundary;
    public SRect BBox;

    /// <summary>Set when the next utterance must start a fresh panel. Port of m_newPanel (panel.h:76).</summary>
    public bool NewPanel { get; protected set; } = true;

    public virtual void StartNewPanel() => NewPanel = true;

    public abstract bool AddLine(uint uID, string words, BalloonMode modes);

    public Panel? RemoveLastPanel()
    {
        if (Panels.Count == 0) return null;
        var p = Panels[^1];
        Panels.RemoveAt(Panels.Count - 1);
        return p;
    }
}

/// <summary>
/// A page laid out as a uniform grid of square panels. Port of CUnitPanelPage (panel.h:96).
/// </summary>
/// <remarks>
/// There is no bin-packing here: the grid is fixed and panels are sized from the window.
/// What varies is <i>how many</i> panels the conversation needs, and that emerges from
/// <see cref="AddLine"/>'s greedy merge-else-split loop.
/// </remarks>
public sealed class UnitPanelPage(LayoutContext ctx, IAvatarDirectory directory) : Page
{
    public LayoutContext Ctx { get; } = ctx;
    public IAvatarDirectory Directory { get; } = directory;

    public int TopY { get; set; }
    public int LeftX { get; set; }

    /// <summary>Supplies the current pose for an avatar when it speaks.</summary>
    public Func<uint, Body>? BodyFactory { get; set; }

    /// <summary>
    /// The backdrop every new panel is created with.
    /// Port of the current/default backdrop id (GetCurrentBackDropID, backdrop.cpp).
    /// </summary>
    /// <remarks>
    /// This has to live on the page, not be stamped onto panels after the fact: panels are
    /// created deep inside <see cref="AddLine"/>'s merge-else-split loop, so a caller that
    /// only updates the panels it can see would leave every later panel blank.
    /// </remarks>
    public ushort CurrentBackDropId { get; set; }

    /// <summary>Backdrop flags applied to new panels, e.g. <see cref="BackDropMode.NoZoom"/>.</summary>
    public BackDropMode CurrentBackDropMode { get; set; }

    /// <summary>Point every panel, present and future, at a backdrop.</summary>
    public void SetBackDrop(ushort backId, BackDropMode mode = BackDropMode.None)
    {
        CurrentBackDropId = backId;
        CurrentBackDropMode = mode;
        foreach (var p in Panels)
        {
            p.BackDrop.BackId = backId;
            p.BackDrop.Mode = mode;
        }
    }

    /// <summary>Overrides how panel seeds are drawn. Leave null for the faithful behaviour.</summary>
    public Func<uint>? SeedFactory { get; set; }

    /// <summary>
    /// Seeds the master stream that panel seeds are drawn from. Port of CChatDoc::m_seed
    /// (chatdoc.cpp:182), which the document sets once and re-seeds from on replay
    /// (chatdoc.cpp:206) so a reloaded comic is identical.
    /// </summary>
    public uint DocumentSeed
    {
        get => _documentSeed;
        set { _documentSeed = value; _master.Srand(value); }
    }

    private uint _documentSeed = 1;
    private readonly CrtRandom _master = new(1);

    /// <summary>A fresh panel carrying the page's current backdrop and a seed from the master stream.</summary>
    private UnitPanel NewUnitPanel() => new()
    {
        Seed = NextSeed(),
        BackDrop = { BackId = CurrentBackDropId, Mode = CurrentBackDropMode },
    };

    /// <summary>Port of CUnitPanelPage::AddPanel (panel.cpp).</summary>
    public bool AddPanel(Panel p)
    {
        Panels.Add(p);
        return true;
    }

    /// <summary>
    /// The number of leading panels framed as establishing shots. Port of Establishing()
    /// (pageview.cpp:823) — the comic opens wide rather than on a close-up.
    /// </summary>
    public int EstablishingPanels { get; set; } = 2;

    /// <summary>
    /// Add one utterance to the comic. Port of CUnitPanelPage::AddLine (panel.cpp:1058).
    /// </summary>
    /// <remarks>
    /// This is the real layout algorithm. It tries to merge the new balloon into the last panel
    /// by cloning it; if the clone cannot be laid out, the panel is the unit of backtracking —
    /// throw it away, start a fresh panel, and recurse. If the text was too tall to fit, the
    /// leftover spills into the next panel by recursing as well. Panel count is therefore an
    /// emergent property, not something anyone computes.
    /// </remarks>
    public override bool AddLine(uint uID, string words, BalloonMode modes)
    {
        if (modes == BalloonMode.Action)
            StartNewPanel();   // action boxes always get their own panel

        // Debugging escape hatch retained from the original (panel.cpp:1067).
        if (words == "<Brk>")
        {
            StartNewPanel();
            return true;
        }

        Panel? pOldP = Panels.Count > 0 ? Panels[^1] : null;
        Panel pNewP;
        bool bReplaceLast;

        // A speaker never speaks twice in one panel, and a panel never holds more than 5 balloons.
        if (NewPanel || pOldP is null ||
            pOldP.Elements.Count >= 5 ||
            Panels.Count < 2 ||
            pOldP.AvatarInPanel(uID))
        {
            pNewP = NewUnitPanel();
            NewPanel = false;
            bReplaceLast = false;
        }
        else
        {
            pNewP = pOldP.Clone();
            bReplaceLast = true;
        }

        var newBalloon = MakeBalloon(words, modes);
        if (newBalloon is null) return false;

        var body = BodyFactory?.Invoke(uID);
        if (body is null) return false;

        pNewP.ReplaceBody(uID, body);
        newBalloon.Speaker = pNewP.FetchSpeaker(uID) ?? body;
        pNewP.Elements.Add(newBalloon);

        if (pNewP is UnitPanel up)
            up.Establishing = Panels.Count < EstablishingPanels;

        pNewP.LayoutAvatars(Ctx, Directory);

        if (!pNewP.LayoutBalloons(Ctx, out string? leftOver))
        {
            // Backtrack at panel granularity: discard and retry in a fresh panel.
            StartNewPanel();
            return AddLine(uID, words, modes);
        }

        if (bReplaceLast) RemoveLastPanel();
        AddPanel(pNewP);
        Directory.ResetAvatar(uID);

        // Text that did not fit spills into the next panel.
        if (!string.IsNullOrEmpty(leftOver))
            AddLine(uID, leftOver, modes);

        return true;
    }

    /// <summary>
    /// Draw the next panel's seed. Port of <c>m_seed = rand()</c> (panel.cpp:556).
    /// </summary>
    /// <remarks>
    /// The seed must come from a random stream, not a counter. The CRT's rand() returns 41 for
    /// seed 1, 18467 for 2 and so on — so a counter would make every panel's <i>first</i>
    /// randfloat() tiny and predictable, and since that first draw picks the balloon width
    /// (GetCloudEstimate, panel.cpp:899), every balloon would come out at its minimum width.
    /// </remarks>
    private uint NextSeed() => SeedFactory?.Invoke() ?? (uint)_master.Rand();

    /// <summary>
    /// Build the right balloon subclass for the mode. Port of CUnitPanelPage::MakeBalloon (panel.h:137).
    /// </summary>
    public Balloon? MakeBalloon(string text, BalloonMode modes)
    {
        var m = Ctx.Metrics;

        if ((modes & BalloonMode.Action) != 0 || (modes & BalloonMode.Sound) != 0)
            return new WoodringBox(text, m.BalloonFont);
        if ((modes & BalloonMode.Whisper) != 0)
            return new WoodringWhisper(text, m.WhisperFont);
        if ((modes & BalloonMode.Think) != 0)
            return new WoodringThink(text, m.BalloonFont);

        return new WoodringNormal(text, m.BalloonFont);
    }

    /// <summary>
    /// Where panel <paramref name="n"/> sits on the page.
    /// Port of the grid arithmetic in CUnitPanelPage::Draw (panel.cpp:1250).
    /// </summary>
    public (int x, int y) GetPanelOrigin(int n)
    {
        var m = Ctx.Metrics;
        int perRow = Math.Max(1, m.PanelsPerRow);
        int col = n % perRow;
        int row = n / perRow;

        int x = LeftX + col * (m.UnitWidth + m.VInterstice);
        int y = TopY - row * (m.UnitHeight + m.HInterstice);
        return (x, y);
    }

    /// <summary>Port of CUnitPanelPage::GetBBox (panel.cpp:1268).</summary>
    public SRect GetPageBBox()
    {
        var m = Ctx.Metrics;
        int perRow = Math.Max(1, m.PanelsPerRow);
        int rows = (Panels.Count + perRow - 1) / perRow;
        int cols = Math.Min(Panels.Count, perRow);

        return new SRect(
            LeftX,
            TopY,
            LeftX + cols * (m.UnitWidth + m.VInterstice),
            TopY - rows * (m.UnitHeight + m.HInterstice));
    }
}
