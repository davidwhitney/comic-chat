namespace ComicChat.Core.Comic;

/// <summary>
/// The ambient state a layout pass needs: text metrics, the panel's random stream,
/// and the page geometry.
/// </summary>
/// <remarks>
/// The original reached for globals here — a process-wide client DC for measurement
/// (GetClientDC()), the CRT's global rand() seeded per panel, and static members on
/// CUnitPanelPage for the grid. Threading an explicit context instead keeps layout pure
/// and testable, which is what lets the engine run headless. The behaviour is unchanged;
/// only the plumbing is.
/// </remarks>
public sealed class LayoutContext(ITextMeasurer measurer, PageMetrics metrics, CrtRandom? rng = null)
{
    public ITextMeasurer Measurer { get; } = measurer;
    public PageMetrics Metrics { get; } = metrics;

    /// <summary>Reseeded per panel from <c>Panel.Seed</c> (panel.cpp:867).</summary>
    public CrtRandom Rng { get; } = rng ?? new CrtRandom();

    public double RandFloat() => Rng.RandFloat();
}

/// <summary>
/// The page grid. Port of the static members of CUnitPanelPage (panel.h:99-105).
/// </summary>
/// <remarks>
/// Panels are always square (SetPanelsWide forces width == height, pageview.cpp:1105)
/// and sized from the window rather than from content.
/// </remarks>
public sealed class PageMetrics
{
    /// <summary>Port of MINUNITPANELWIDTH (panel.h:152).</summary>
    public const int MinUnitPanelWidth = 2300;
    public const int MinUnitPanelHeight = MinUnitPanelWidth;

    public int PanelsPerRow { get; set; } = 2;

    /// <summary>Negative means no limit — the page never ends (panel.cpp:58).</summary>
    public int PanelsPerColumn { get; set; } = -1;

    public int UnitWidth { get; set; } = MinUnitPanelWidth;
    public int UnitHeight { get; set; } = MinUnitPanelHeight;

    public int HInterstice { get; set; } = 144;
    public int VInterstice { get; set; } = 144;

    /// <summary>Port of CUnitPanel::m_borderWidth (panel.cpp:64). The pen is drawn at 2x this.</summary>
    public int BorderWidth { get; set; } = 60;

    public FontInfo BalloonFont { get; set; } = new();
    public FontInfo WhisperFont { get; set; } = new();
    public FontInfo ShoutFont { get; set; } = new();
    public FontInfo TitleFont { get; set; } = new();

    /// <summary>
    /// Port of the sizing in CPageView::SetPanelsWide (pageview.cpp:1101) — clamp to the
    /// minimum and force square panels.
    /// </summary>
    public void SetPanelsWide(int n, int availableWidth)
    {
        PanelsPerRow = Math.Max(1, n);
        int goal = (availableWidth - (PanelsPerRow + 1) * VInterstice) / PanelsPerRow;
        goal = Math.Max(goal, MinUnitPanelWidth);
        UnitWidth = goal;
        UnitHeight = goal;
    }
}
